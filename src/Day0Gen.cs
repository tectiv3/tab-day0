// Day0Gen — day-0 survival save generator for They Are Billions (v1.0.14).
// Mirrors the game's own survival/community-challenge start handler via runtime
// reflection only (zero compile-time game references). See SPEC.md / PLAN.md.
//
// C# 5 compatible, target net48. Must be run from the TAB install directory
// (operator places the exe there) so Assembly.Load("TheyAreBillions") resolves.
// Compile-time deps: System plus in-box System.Windows.Forms - the engine is
// WinForms (DXVision) and its state is thread-affine, so the construct/generate/
// save sequence must be marshaled onto the engine's UI thread (see
// FindEngineUiMarshalTarget / RunConstructGenerateSave).
//
// Phases: discovery | zombie | genprobe (generator diagnostic) | full (later
// phases imply earlier ones; genprobe is a read-only diagnostic, see RunGenProbe).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Day0Gen
{
    // ---------------------------------------------------------------------------
    // Logging: console + Day0Gen.log (current working directory; the only file,
    // besides the two save artifacts, this tool is ever allowed to write).
    // ---------------------------------------------------------------------------
    internal static class Log
    {
        private static readonly object gate = new object();
        private static StreamWriter writer;

        public static void Open()
        {
            try
            {
                string path = Path.Combine(Environment.CurrentDirectory, "Day0Gen.log");
                writer = new StreamWriter(path, true, Encoding.UTF8) { AutoFlush = true };
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("WARNING: cannot open Day0Gen.log: " + e.Message);
                writer = null;
            }
        }

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message;
            lock (gate)
            {
                Console.WriteLine(line);
                if (writer != null)
                {
                    try { writer.WriteLine(line); } catch { }
                }
            }
        }
    }

    internal sealed class Day0GenException : Exception
    {
        public Day0GenException(string message) : base(message) { }
        public Day0GenException(string message, Exception inner) : base(message, inner) { }
    }

    // ---------------------------------------------------------------------------
    // CLI options
    // ---------------------------------------------------------------------------
    internal sealed class Options
    {
        public string Phase = null;                     // discovery | zombie | genprobe | full
        public string TabDir = @"C:\Program Files (x86)\Steam\steamapps\common\They Are Billions";
        public string SavesDir = null;                  // null => default / manager-discovered
        public bool SavesDirExplicit = false;
        public int Seed = 550040233;
        public int NCells = 256;
        public float Duration = 1.0f;
        public float Pop = 1.0f;
        public string Name = "CC 550040233";
        public string ValidateSigner = null;

        public string DefaultSavesDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "They Are Billions", "Saves");
        }

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            // Every flag takes exactly one value; consume args in pairs.
            int i = 0;
            while (i < args.Length)
            {
                string a = args[i];
                bool hasVal = (i + 1) < args.Length;
                string v = hasVal ? args[i + 1] : null;
                if (!hasVal)
                    throw new Day0GenException("Missing value for " + a);
                if (a == "--phase") o.Phase = v;
                else if (a == "--tab-dir") o.TabDir = v;
                else if (a == "--saves-dir") { o.SavesDir = v; o.SavesDirExplicit = true; }
                else if (a == "--seed") o.Seed = ParseInt(v, a);
                else if (a == "--ncells") o.NCells = ParseInt(v, a);
                else if (a == "--duration") o.Duration = ParseFloat(v, a);
                else if (a == "--pop") o.Pop = ParseFloat(v, a);
                else if (a == "--name") o.Name = v;
                else if (a == "--validate-signer") o.ValidateSigner = v;
                else throw new Day0GenException("Unknown argument: " + a);
                i += 2;
            }
            if (o.Phase == null)
                throw new Day0GenException("--phase discovery|zombie|genprobe|full is required");
            o.Phase = o.Phase.ToLowerInvariant();
            if (o.Phase != "discovery" && o.Phase != "zombie" && o.Phase != "genprobe" && o.Phase != "full")
                throw new Day0GenException("Invalid --phase '" + o.Phase + "' (discovery|zombie|genprobe|full)");
            if (o.NCells < 64 || o.NCells > 512)
                throw new Day0GenException("--ncells out of range [64,512]");
            if (o.Name == null || o.Name.Trim().Length == 0)
                throw new Day0GenException("--name must not be empty");
            if (o.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new Day0GenException("--name contains characters invalid in a file name");
            return o;
        }

        private static int ParseInt(string s, string flag)
        {
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                throw new Day0GenException(flag + " expects an integer, got: " + s);
            return v;
        }

        private static float ParseFloat(string s, string flag)
        {
            float v;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new Day0GenException(flag + " expects a number, got: " + s);
            return v;
        }
    }

    // ---------------------------------------------------------------------------
    // Directory snapshot (SHA256 of every file) for before/after forensics.
    // Read-only.
    // ---------------------------------------------------------------------------
    internal sealed class DirSnapshot
    {
        public readonly Dictionary<string, string> Hashes = new Dictionary<string, string>();

        public static DirSnapshot Take(string dir)
        {
            DirSnapshot s = new DirSnapshot();
            TakeInto(s, dir);
            return s;
        }

        private static void TakeInto(DirSnapshot s, string dir)
        {
            if (!Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
            catch (Exception e)
            {
                Log.Write("SNAPSHOT: cannot enumerate " + dir + ": " + e.Message);
                return;
            }
            foreach (string f in files)
            {
                string key = f;
                string hash;
                try
                {
                    using (FileStream fs = File.OpenRead(f))
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] h = sha.ComputeHash(fs);
                        StringBuilder sb = new StringBuilder(h.Length * 2);
                        foreach (byte b in h) sb.Append(b.ToString("x2"));
                        hash = sb.ToString();
                    }
                }
                catch (Exception e)
                {
                    hash = "UNREADABLE: " + e.GetType().Name;
                }
                s.Hashes[key] = hash;
            }
        }

        public void Add(string dir) { TakeInto(this, dir); }

        // Returns list of human-readable change lines.
        public static List<string> Diff(DirSnapshot before, DirSnapshot after)
        {
            List<string> changes = new List<string>();
            foreach (KeyValuePair<string, string> kv in before.Hashes)
            {
                string afterHash;
                if (!after.Hashes.TryGetValue(kv.Key, out afterHash))
                    changes.Add("DELETED: " + kv.Key);
                else if (afterHash != kv.Value)
                    changes.Add("CHANGED: " + kv.Key);
            }
            foreach (KeyValuePair<string, string> kv in after.Hashes)
            {
                if (!before.Hashes.ContainsKey(kv.Key))
                    changes.Add("CREATED: " + kv.Key);
            }
            return changes;
        }
    }

    // ---------------------------------------------------------------------------
    // Reflection layer. Every lookup logs the matched strategy; every invocation
    // logs target/args/result/exception.
    // ---------------------------------------------------------------------------
    internal sealed class GameReflector
    {
        // Obfuscated identifiers, in ILSpy-escaped form exactly as they appear in
        // vendor/decompiled for build v1.0.14. Unescape() turns them into the real
        // metadata names (#,=,$ chars) at runtime.
        private const string N_MANAGER_TYPE = "_0023_003Dz4RevDP3eECXqXS6JRA_003D_003D";
        private const string N_MANAGER_CURRENT = "_0023_003Dzuartwoo_003D";
        private const string N_SET = "_0023_003Dz2SXmL2Q_003D";
        private const string N_LEVEL_INIT = "_0023_003DzF1YHTRUZSNRa";
        private const string N_SIGNING = "_0023_003DzX5dOGo9W_0024Dop";
        private const string N_FLAG = "_0023_003Dz4Qt7c_0024gkgLZHT7ayvA_003D_003D";
        private const string N_PWD_SET = "_0023_003DzpKDARrtdO1GK7shdnQ_003D_003D";
        private const string N_PWD_CLEAR = "_0023_003DzvgSfu3ouG_TLllPQAA_003D_003D";
        private const string N_SAVEWRITER = "_0023_003DzMtGuEM2lBSlZ5BGWvg_003D_003D";
        private const string N_SAVES_FOLDER = "_0023_003DzND5ul2zfzAnWdSnC0A_003D_003D";
        private const string N_SAVE_LIST = "_0023_003DzegKkTm3kHc6FhM_EOg_003D_003D";
        private const string N_STATE_INFO = "_0023_003DzHhDw0V62_0024fqG";
        private const string N_ZXFILE_READ = "_0023_003DzyuVTytDlXbMA";
        private const string N_GENERATOR_TYPE = "_0023_003Dzyl_NPjjlA7DRfVtsRJCX1kN4BxSr";
        private const string N_GENERATE = "_0023_003DzEzgd90E_003D";
        private const string N_THEME_TABLE = "_0023_003Dz4k5FO_0024EclQhr";
        private const string N_GAMESYS_TYPE = "_0023_003DzxRcpu6e7NYzT7tGWqPjpOkc_003D";
        private const string N_SET_LEVEL = "_0023_003DzmTU4kueQctVr";
        private const string N_TABLE_LOADER_TYPE = "_0023_003Dz3Zxcp6RwVCZHa9xpeg_003D_003D";
        private const string N_TABLE_LOAD = "_0023_003DzUoK3qsRYSJTT";

        public Assembly TabAssembly;
        public Assembly DxAssembly;

        // Types
        public Type ProgramType, ManagerType, GameStateType, LevelStateType, ParamsType,
                    GameStateInfoType, GeneratorType, MapThemeType, GameSystemType,
                    TableLoaderType, DxSystemType, ZipSerializerType, DxLevelType,
                    FileGenericBase;
        public Type GameModeEnum, MapThemeEnum, ChallengeEnum;

        // DXVision.DXProject / .Current: the engine's scene/project context singleton.
        // Current may be exposed as a property or a field depending on the build.
        public Type DxProjectType;
        public PropertyInfo DxProjectCurrentProp;
        public FieldInfo DxProjectCurrentField;

        // Read-only diagnostics surface (Program.LogProjectDiagnostics). None of these
        // drive generation; they exist solely to explain the generator's DXProject NRE.
        public MethodInfo DxProjectFromIdMethod;
        public FieldInfo DxProjectLoadedProjectsField;
        public FieldInfo DxProjectLastLoadedField;
        public FieldInfo DxProjectSGameFilesField;
        public FieldInfo DxProjectSGameDirectoryField;

        // Program
        public MethodInfo MainMethod;

        // Manager
        public MethodInfo ManagerCurrentMethod;      // static -> manager
        public PropertyInfo GameAccountProp;         // instance
        public PropertyInfo CurrentGameSystemProp;   // instance
        public MethodInfo SigningMethod;             // static (string,int) -> string
        public MethodInfo FlagMethod;                // static (string) -> int
        public MethodInfo PwdSetMethod;              // static (string,int,bool) -> void
        public MethodInfo PwdClearMethod;            // static (string,int,bool) -> void
        public MethodInfo SaveWriterMethod;          // instance (string) -> void  [game-native full save]
        public MethodInfo SavesFolderMethod;         // static () -> string
        public MethodInfo SaveListMethod;            // instance () -> List<ZXGameStateInfo>

        // ZXGameState / ZXLevelState
        public MethodInfo GameStateSetMethod, GameStateCurrentMethod;
        public MethodInfo LevelStateSetMethod, LevelStateCurrentMethod, LevelStateInitMethod;
        public ConstructorInfo GameStateCtorName;

        // ZXGameStateInfo from state
        public MethodInfo StateInfoMethod;           // instance (string) -> ZXGameStateInfo

        // ZXFile<T> reader (static on generic type)
        public MethodInfo ZxFileReadMethod;          // static (string) -> T

        // Generator + tables
        public MethodInfo GenerateMethod;            // static (ZXRandomLevelParams) -> DXLevel
        public MethodInfo ThemeTableMethod;          // static () -> Dictionary
        public MethodInfo TableLoadMethod;           // static () -> void

        // Game system
        public MethodInfo SetLevelMethod;            // instance (DXLevel) -> void
        public MethodInfo DxSystemLoadMethod;        // static generic (bool) -> T

        // DXVision.Serialization.ZipSerializer
        public PropertyInfo ZipCurrentProp, ZipPasswordProp;
        public MethodInfo ZipReadMethod;             // static (string,string) -> object
        public MethodInfo ZipWriteMethod;            // static (string,string,object,string,object) -> void

        // -----------------------------------------------------------------------
        // ILSpy identifier unescaping: _XXXX (4 hex digits) -> char 0xXXXX.
        // Literals like "_w" or "_EOg" (non-hex tail) stay untouched.
        // -----------------------------------------------------------------------
        public static string Unescape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '_' && i + 4 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]) && IsHex(s[i + 3]) && IsHex(s[i + 4]))
                {
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                    i += 5;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        // -----------------------------------------------------------------------
        // Loading
        // -----------------------------------------------------------------------
        public void LoadAssemblies()
        {
            Log.Write("Loading assembly TheyAreBillions ...");
            TabAssembly = Assembly.Load("TheyAreBillions");
            Log.Write("Loaded: " + TabAssembly.FullName);

            // DXVision is a separate assembly with no file on disk: it is embedded in
            // TheyAreBillions.exe and only materialized by the Eazfuscator AssemblyResolve
            // handler installed by the TheyAreBillions module initializer. Run that
            // initializer before any GetTypes() and before Assembly.Load("DXVision").
            bool moduleCtorRan = true;
            try
            {
                RuntimeHelpers.RunModuleConstructor(TabAssembly.ManifestModule.ModuleHandle);
                Log.Write("Ran TheyAreBillions module initializer (embedded-assembly resolver installed).");
            }
            catch (Exception ex)
            {
                moduleCtorRan = false;
                Log.Write("ERROR: TheyAreBillions module initializer failed (" + ex.GetType().Name + ": " +
                          ex.Message + "); the embedded DXVision assembly will NOT load and all " +
                          "DXVision.* types will be unavailable.");
            }

            try
            {
                DxAssembly = Assembly.Load("DXVision");
                Log.Write("Loaded: " + DxAssembly.FullName);
            }
            catch (Exception ex)
            {
                DxAssembly = TabAssembly;
                Log.Write("WARNING: could not load DXVision assembly (" + ex.GetType().Name + ": " + ex.Message +
                          "); falling back to the TheyAreBillions assembly. DXVision.* types will be UNAVAILABLE.");
                if (!moduleCtorRan)
                    Log.Write("WARNING: fallback is expected to fail: the module initializer that installs the " +
                              "DXVision resolver did not run.");
            }
        }

        private static string Sig(MethodBase m)
        {
            ParameterInfo[] ps = m.GetParameters();
            StringBuilder sb = new StringBuilder();
            sb.Append(m is MethodInfo ? ((MethodInfo)m).ReturnType.Name : "void");
            sb.Append(" ");
            sb.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?");
            sb.Append("::");
            sb.Append(m.Name);
            sb.Append("(");
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.Name);
            }
            sb.Append(")");
            return sb.ToString();
        }

        private static string MType(MethodInfo m)
        {
            return (m.IsStatic ? "static " : "instance ") +
                   (m.IsPublic ? "public" : (m.IsFamily || m.IsFamilyOrAssembly ? "protected" : "nonpublic"));
        }

        private void Found(string purpose, string strategy, MethodInfo m)
        {
            Log.Write("FOUND [" + strategy + "] " + purpose + " -> " + MType(m) + " " + Sig(m));
        }

        private void Found(string purpose, string strategy, Type t)
        {
            Log.Write("FOUND [" + strategy + "] " + purpose + " -> type " + t.FullName +
                      (t.IsPublic ? " (public)" : " (internal)"));
        }

        private void Found(string purpose, string strategy, ConstructorInfo c, Type declaring)
        {
            Log.Write("FOUND [" + strategy + "] " + purpose + " -> ctor on " + declaring.Name + Sig(c));
        }

        private void Found(string purpose, string strategy, PropertyInfo p)
        {
            Log.Write("FOUND [" + strategy + "] " + purpose + " -> property " +
                      (p.DeclaringType != null ? p.DeclaringType.Name : "?") + "." + p.Name +
                      " : " + p.PropertyType.Name);
        }

        private void Found(string purpose, string strategy, FieldInfo f)
        {
            Log.Write("FOUND [" + strategy + "] " + purpose + " -> field " +
                      (f.DeclaringType != null ? f.DeclaringType.Name : "?") + "." + f.Name +
                      " : " + f.FieldType.Name + (f.IsStatic ? " (static)" : ""));
        }

        // -----------------------------------------------------------------------
        // Discovery. Locates every target; throws Day0GenException on hard misses.
        // -----------------------------------------------------------------------
        public void DiscoverAll(bool requireEngineOnlyTargets)
        {
            // --- Engine entry ---------------------------------------------------
            ProgramType = TabAssembly.GetType("ZX.Program", false);
            if (ProgramType == null)
                throw new Day0GenException("Type ZX.Program not found in TheyAreBillions assembly");
            MainMethod = ProgramType.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic);
            if (MainMethod == null)
                throw new Day0GenException("ZX.Program.Main(string[]) static nonpublic not found");
            Found("engine entry Main", "public-stable-name", MainMethod);

            // --- Manager class (type owning get_GameAccount, TABSAT pattern) -----
            foreach (Type t in SafeGetTypes(TabAssembly))
            {
                if (t == null) continue;
                MethodInfo ga = null;
                try { ga = t.GetMethod("get_GameAccount"); } catch { }
                if (ga != null)
                {
                    ManagerType = t;
                    Found("manager class (owner of get_GameAccount)", "manager-scan", t);
                    Found("GameAccount property", "manager-scan", ga);
                    GameAccountProp = t.GetProperty("GameAccount");
                    break;
                }
            }
            if (ManagerType == null)
                throw new Day0GenException("Manager class (get_GameAccount owner) not found");
            if (GameAccountProp == null)
                throw new Day0GenException("GameAccount property not found on manager");

            string mgrRealName = Unescape(N_MANAGER_TYPE);
            if (ManagerType.Name != mgrRealName)
                Log.Write("NOTE: manager type name is '" + ManagerType.Name + "', expected '" + mgrRealName +
                          "' from v1.0.14 map (build differs from decompile?)");

            ManagerCurrentMethod = ManagerType.GetMethod(Unescape(N_MANAGER_CURRENT),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (ManagerCurrentMethod == null || ManagerCurrentMethod.ReturnType != ManagerType)
                throw new Day0GenException("Manager singleton accessor " + N_MANAGER_CURRENT + " not found");
            Found("manager current/singleton", "exact-name", ManagerCurrentMethod);

            CurrentGameSystemProp = ManagerType.GetProperty("CurrentGameSystem");
            if (CurrentGameSystemProp == null || !CurrentGameSystemProp.CanWrite)
                throw new Day0GenException("Manager.CurrentGameSystem settable property not found");
            Found("manager CurrentGameSystem property", "public-stable-name", CurrentGameSystemProp);

            // --- Signing / password machinery (manager statics) ------------------
            SigningMethod = ManagerType.GetMethod(Unescape(N_SIGNING),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (SigningMethod != null) Found("zxcheck signing (file,2)->string", "exact-name", SigningMethod);
            if (SigningMethod == null)
            {
                SigningMethod = ScanMethod(ManagerType, "signing",
                    delegate(MethodInfo m)
                    {
                        if (!m.IsStatic || m.IsPublic) return false;
                        ParameterInfo[] p = m.GetParameters();
                        return p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(int)
                               && m.ReturnType == typeof(string);
                    });
                if (SigningMethod == null)
                    throw new Day0GenException("Signing method not found (exact name + signature scan failed)");
                Found("zxcheck signing (file,2)->string", "signature-scan", SigningMethod);
            }

            FlagMethod = ManagerType.GetMethod(Unescape(N_FLAG),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (FlagMethod != null) Found("password flag (string)->int", "exact-name", FlagMethod);
            if (FlagMethod == null)
            {
                FlagMethod = ScanMethod(ManagerType, "flag",
                    delegate(MethodInfo m)
                    {
                        if (!m.IsStatic || m.IsPublic) return false;
                        ParameterInfo[] p = m.GetParameters();
                        return p.Length == 1 && p[0].ParameterType == typeof(string)
                               && m.ReturnType == typeof(int);
                    });
                if (FlagMethod == null)
                    throw new Day0GenException("Password flag method not found");
                Found("password flag (string)->int", "signature-scan", FlagMethod);
            }

            PwdSetMethod = ManagerType.GetMethod(Unescape(N_PWD_SET),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (PwdSetMethod != null) Found("password setter (string,int,bool)->void", "exact-name", PwdSetMethod);
            PwdClearMethod = ManagerType.GetMethod(Unescape(N_PWD_CLEAR),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (PwdClearMethod != null) Found("password clearer (string,int,bool)->void", "exact-name", PwdClearMethod);
            if (PwdSetMethod == null || PwdClearMethod == null)
            {
                // TABSAT-style fallback: probe all (string,int,bool)->void statics;
                // the setter is the one that yields a non-empty ZipSerializer password.
                Log.Write("Password set/clear exact names incomplete; falling back to TABSAT probe scan ...");
                EnsureZipSerializer();
                List<MethodInfo> candidates = new List<MethodInfo>();
                foreach (MethodInfo m in ManagerType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 3 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(int)
                        && p[2].ParameterType == typeof(bool) && m.ReturnType == typeof(void))
                        candidates.Add(m);
                }
                // also scan types having ProcessSpecialKeys_KeyUp like TABSAT did
                foreach (Type t in SafeGetTypes(TabAssembly))
                {
                    if (t == null || t == ManagerType) continue;
                    MethodInfo k = null;
                    try { k = t.GetMethod("ProcessSpecialKeys_KeyUp", BindingFlags.Instance | BindingFlags.NonPublic); } catch { }
                    if (k == null) continue;
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
                    {
                        ParameterInfo[] p = m.GetParameters();
                        if (p.Length == 3 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(int)
                            && p[2].ParameterType == typeof(bool) && m.ReturnType == typeof(void))
                            candidates.Add(m);
                    }
                }
                foreach (MethodInfo cand in candidates)
                {
                    if (PwdSetMethod != null && PwdClearMethod != null) break;
                    if (PwdSetMethod == null && ProbePasswordCandidate(cand))
                    {
                        PwdSetMethod = cand;
                        Found("password setter (string,int,bool)->void", "signature-scan+probe", cand);
                    }
                }
                if (PwdSetMethod == null)
                    throw new Day0GenException("Password setter not found by exact name or probe scan");
                if (PwdClearMethod == null)
                {
                    foreach (MethodInfo cand in candidates)
                    {
                        if (cand != PwdSetMethod)
                        {
                            PwdClearMethod = cand;
                            Found("password clearer (string,int,bool)->void", "signature-scan(remaining)", cand);
                            break;
                        }
                    }
                }
            }

            // --- Save I/O --------------------------------------------------------
            SaveWriterMethod = ManagerType.GetMethod(Unescape(N_SAVEWRITER),
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (SaveWriterMethod != null) Found("game-native save writer (path)->void [writes zxsav+zxcheck]", "exact-name", SaveWriterMethod);
            else Log.Write("NOTE: game-native save writer not found by exact name; manual composition fallback will be used");

            SavesFolderMethod = ManagerType.GetMethod(Unescape(N_SAVES_FOLDER),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (SavesFolderMethod == null)
            {
                SavesFolderMethod = ManagerType.GetMethod(Unescape(N_SAVES_FOLDER),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (SavesFolderMethod == null)
                throw new Day0GenException("Saves-folder method not found on manager");
            Found("saves folder () -> string", "exact-name", SavesFolderMethod);

            SaveListMethod = ManagerType.GetMethod(Unescape(N_SAVE_LIST),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (SaveListMethod == null)
                throw new Day0GenException("Save-list method not found on manager");
            Found("save list () -> List<ZXGameStateInfo>", "exact-name", SaveListMethod);

            // --- Game state / level state ---------------------------------------
            GameStateType = TabAssembly.GetType("ZX.ZXGameState", false);
            if (GameStateType == null) throw new Day0GenException("Type ZX.ZXGameState not found");
            Found("game state type", "public-stable-name", GameStateType);

            GameStateSetMethod = GameStateType.GetMethod(Unescape(N_SET), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            GameStateCurrentMethod = GameStateType.GetMethod(Unescape(N_MANAGER_CURRENT), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (GameStateSetMethod == null || GameStateCurrentMethod == null)
                throw new Day0GenException("ZXGameState Set/Current not found");
            Found("ZXGameState.Set(state)", "exact-name", GameStateSetMethod);
            Found("ZXGameState.Current()", "exact-name", GameStateCurrentMethod);

            foreach (ConstructorInfo c in GameStateType.GetConstructors())
            {
                ParameterInfo[] p = c.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(string))
                {
                    GameStateCtorName = c;
                    Found("ZXGameState ctor(string)", "signature", c, GameStateType);
                    break;
                }
            }
            if (GameStateCtorName == null)
                throw new Day0GenException("ZXGameState ctor(string) not found");

            foreach (string pn in new string[] { "Name", "GameMode", "SurvivalModeParams", "ChallengeType", "ChallengeID" })
            {
                PropertyInfo p = GameStateType.GetProperty(pn);
                if (p == null || !p.CanWrite)
                    throw new Day0GenException("ZXGameState property missing/not writable: " + pn);
                Found("ZXGameState." + pn, "public-stable-name", p);
            }

            LevelStateType = TabAssembly.GetType("ZX.ZXLevelState", false);
            if (LevelStateType == null) throw new Day0GenException("Type ZX.ZXLevelState not found");
            Found("level state type", "public-stable-name", LevelStateType);
            LevelStateSetMethod = LevelStateType.GetMethod(Unescape(N_SET), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            LevelStateCurrentMethod = LevelStateType.GetMethod(Unescape(N_MANAGER_CURRENT), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            LevelStateInitMethod = LevelStateType.GetMethod(Unescape(N_LEVEL_INIT), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (LevelStateSetMethod == null || LevelStateCurrentMethod == null || LevelStateInitMethod == null)
                throw new Day0GenException("ZXLevelState Set/Current/Init not found");
            Found("ZXLevelState.Set(state)", "exact-name", LevelStateSetMethod);
            Found("ZXLevelState.Current()", "exact-name", LevelStateCurrentMethod);
            Found("ZXLevelState.Init()", "exact-name", LevelStateInitMethod);

            StateInfoMethod = GameStateType.GetMethod(Unescape(N_STATE_INFO),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (StateInfoMethod != null) Found("ZXGameState build-info(filename) -> ZXGameStateInfo", "exact-name", StateInfoMethod);
            else Log.Write("NOTE: ZXGameState info-builder not found (only needed for manual save fallback)");

            // --- Level params -----------------------------------------------------
            ParamsType = TabAssembly.GetType("ZX.GameSystems.ZXRandomLevelParams", false);
            if (ParamsType == null) throw new Day0GenException("Type ZX.GameSystems.ZXRandomLevelParams not found");
            Found("random level params type", "public-stable-name", ParamsType);
            foreach (string pn in new string[] { "Seed", "NCells", "ThemeType", "FactorGameDuration",
                                                 "FactorZombiePopulation", "Name" })
            {
                PropertyInfo p = ParamsType.GetProperty(pn);
                if (p == null || !p.CanWrite)
                    throw new Day0GenException("ZXRandomLevelParams property missing/not writable: " + pn);
                Found("ZXRandomLevelParams." + pn, "public-stable-name", p);
            }
            // DifficultyType is deliberately NOT set (default None), but verify it exists.
            if (ParamsType.GetProperty("DifficultyType") == null)
                throw new Day0GenException("ZXRandomLevelParams.DifficultyType missing");
            if (ParamsType.GetConstructor(new Type[0]) == null)
                throw new Day0GenException("ZXRandomLevelParams ctor() not found");

            // --- Enums ------------------------------------------------------------
            GameModeEnum = TabAssembly.GetType("ZX.ZXGameModeType", false);
            MapThemeEnum = TabAssembly.GetType("ZX.ZXMapThemeType", false);
            ChallengeEnum = TabAssembly.GetType("ZX.ZXGameChallengeType", false);
            if (GameModeEnum == null || MapThemeEnum == null || ChallengeEnum == null
                || !Enum.IsDefined(GameModeEnum, "Survival")
                || !Enum.IsDefined(MapThemeEnum, "None")
                || !Enum.IsDefined(ChallengeEnum, "Default"))
                throw new Day0GenException("Enums ZXGameModeType/ZXMapThemeType/ZXGameChallengeType incomplete");
            Found("enum ZXGameModeType.Survival", "public-stable-name", GameModeEnum);
            Found("enum ZXMapThemeType.None", "public-stable-name", MapThemeEnum);
            Found("enum ZXGameChallengeType.Default", "public-stable-name", ChallengeEnum);

            // --- Generator ----------------------------------------------------------
            GeneratorType = TabAssembly.GetType(Unescape(N_GENERATOR_TYPE), false);
            string genStrategy = "exact-name";
            if (GeneratorType == null)
            {
                genStrategy = "signature-scan";
                foreach (Type t in SafeGetTypes(TabAssembly))
                {
                    if (t == null) continue;
                    if (FindGeneratorOn(t) != null) { GeneratorType = t; break; }
                }
                if (GeneratorType == null)
                    throw new Day0GenException("Generator type not found");
            }
            GenerateMethod = FindGeneratorOn(GeneratorType);
            if (GenerateMethod == null)
                throw new Day0GenException("Generator method (ZXRandomLevelParams)->DXLevel not found on " + GeneratorType.Name);
            Found("random level generator", genStrategy, GenerateMethod);

            // --- Theme table + table loader -----------------------------------------
            MapThemeType = TabAssembly.GetType("ZX.GameSystems.ZXMapTheme", false);
            if (MapThemeType == null) throw new Day0GenException("Type ZX.GameSystems.ZXMapTheme not found");
            Found("map theme type", "public-stable-name", MapThemeType);
            ThemeTableMethod = MapThemeType.GetMethod(Unescape(N_THEME_TABLE),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (ThemeTableMethod == null)
                throw new Day0GenException("Theme table method not found on ZXMapTheme");
            Found("theme table () -> Dictionary", "exact-name", ThemeTableMethod);

            TableLoaderType = TabAssembly.GetType(Unescape(N_TABLE_LOADER_TYPE), false);
            if (TableLoaderType != null)
            {
                TableLoadMethod = TableLoaderType.GetMethod(Unescape(N_TABLE_LOAD),
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (TableLoadMethod != null)
                    Found("table loader (Tables Excel Read)", "exact-name", TableLoadMethod);
            }
            if (TableLoadMethod == null)
                Log.Write("NOTE: table loader method not found by exact name; empty-table fallback unavailable");

            // --- Game system + DXSystem ----------------------------------------------
            GameSystemType = TabAssembly.GetType(Unescape(N_GAMESYS_TYPE), false);
            string gsStrategy = "exact-name";
            if (GameSystemType == null)
            {
                gsStrategy = "signature-scan(SetLevel owner)";
                DxLevelType = DxAssembly.GetType("DXVision.DXLevel", false);
                if (DxLevelType == null) throw new Day0GenException("Type DXVision.DXLevel not found");
                foreach (Type t in SafeGetTypes(TabAssembly))
                {
                    if (t == null) continue;
                    MethodInfo sl = t.GetMethod(Unescape(N_SET_LEVEL),
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (sl != null && sl.GetParameters().Length == 1 && sl.GetParameters()[0].ParameterType == DxLevelType)
                    {
                        GameSystemType = t;
                        break;
                    }
                }
                if (GameSystemType == null)
                    throw new Day0GenException("Game system type not found");
            }
            Found("game system type", gsStrategy, GameSystemType);

            DxLevelType = DxAssembly.GetType("DXVision.DXLevel", false);
            if (DxLevelType == null) throw new Day0GenException("Type DXVision.DXLevel not found");
            SetLevelMethod = GameSystemType.GetMethod(Unescape(N_SET_LEVEL),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (SetLevelMethod == null || SetLevelMethod.GetParameters().Length != 1
                || SetLevelMethod.GetParameters()[0].ParameterType != DxLevelType)
                throw new Day0GenException("SetLevel(DXLevel) not found on game system type");
            Found("game system SetLevel(DXLevel)", gsStrategy == "exact-name" ? "exact-name" : "signature-scan", SetLevelMethod);

            DxSystemType = DxAssembly.GetType("DXVision.DXSystem", false);
            if (DxSystemType == null) throw new Day0GenException("Type DXVision.DXSystem not found");
            Found("DXSystem type", "public-stable-name", DxSystemType);
            foreach (MethodInfo m in DxSystemType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "Load" || !m.IsGenericMethodDefinition) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(bool))
                {
                    DxSystemLoadMethod = m;
                    break;
                }
            }
            if (DxSystemLoadMethod == null)
                throw new Day0GenException("DXSystem.Load<T>(bool) not found");
            Found("DXSystem.Load<T>(bool)", "signature-scan", DxSystemLoadMethod);

            // --- DXProject (engine scene/project context) --------------------------------
            // ZXLevelState's ctor reads DXProject.Current; it is populated by the engine's
            // scene/project init. Absence is not fatal here (the engine may not have reached
            // that point yet) - WaitForProjectContext polls it before construction.
            DxProjectType = DxAssembly.GetType("DXVision.DXProject", false);
            if (DxProjectType != null)
            {
                Found("DXProject type", "public-stable-name", DxProjectType);

                // Diagnostic: enumerate the public static surface so the real API is visible
                // in the log without needing another interactive run.
                StringBuilder members = new StringBuilder();
                foreach (PropertyInfo p in DxProjectType.GetProperties(BindingFlags.Static | BindingFlags.Public))
                {
                    if (members.Length > 0) members.Append(", ");
                    members.Append(p.PropertyType.Name).Append(" ").Append(p.Name);
                }
                foreach (FieldInfo f in DxProjectType.GetFields(BindingFlags.Static | BindingFlags.Public))
                {
                    if (members.Length > 0) members.Append(", ");
                    members.Append(f.FieldType.Name).Append(" ").Append(f.Name);
                }
                Log.Write("DXProject public static members: " +
                          (members.Length == 0 ? "(none)" : members.ToString()));

                DxProjectCurrentProp = DxProjectType.GetProperty("Current",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (DxProjectCurrentProp != null)
                {
                    Found("DXProject.Current (static)", "public-stable-name", DxProjectCurrentProp);
                }
                else
                {
                    DxProjectCurrentField = DxProjectType.GetField("Current",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (DxProjectCurrentField != null)
                        Found("DXProject.Current (static)", "public-stable-name", DxProjectCurrentField);
                    else
                        Log.Write("NOTE: DXVision.DXProject.Current (property or field) not found; " +
                                  "WaitForProjectContext cannot verify engine init.");
                }

                DiscoverDxProjectDiagnosticMembers();
            }
            else
            {
                Log.Write("NOTE: DXVision.DXProject type not found; ZXLevelState construction may fail " +
                          "with NullReferenceException if the engine project context is missing.");
            }

            // --- ZipSerializer ----------------------------------------------------------
            EnsureZipSerializer();
            Found("ZipSerializer type", "public-stable-name", ZipSerializerType);
            ZipCurrentProp = ZipSerializerType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public);
            ZipPasswordProp = ZipSerializerType.GetProperty("Password", BindingFlags.Instance | BindingFlags.Public);
            if (ZipCurrentProp == null || ZipPasswordProp == null)
                throw new Day0GenException("ZipSerializer.Current/Password properties not found");
            Found("ZipSerializer.Current (static)", "TABSAT-pattern", ZipCurrentProp);
            Found("ZipSerializer.Password (instance)", "TABSAT-pattern", ZipPasswordProp);

            foreach (MethodInfo m in ZipSerializerType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == "Read" && p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string))
                    ZipReadMethod = m;
                if (m.Name == "Write" && p.Length == 5 && p[0].ParameterType == typeof(string)
                    && p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(object)
                    && p[3].ParameterType == typeof(string) && p[4].ParameterType == typeof(object))
                    ZipWriteMethod = m;
            }
            if (ZipReadMethod != null) Found("ZipSerializer.Read(path,entry)", "signature-scan", ZipReadMethod);
            if (ZipWriteMethod != null) Found("ZipSerializer.Write(path,k1,v1,k2,v2)", "signature-scan", ZipWriteMethod);
            if (ZipWriteMethod == null && SaveWriterMethod == null)
                throw new Day0GenException("Neither native save writer nor ZipSerializer.Write available");

            // --- ZXGameStateInfo + ZXFile<T> reader -------------------------------------
            GameStateInfoType = TabAssembly.GetType("ZX.ZXGameStateInfo", false);
            if (GameStateInfoType == null) throw new Day0GenException("Type ZX.ZXGameStateInfo not found");
            Found("game state info type", "public-stable-name", GameStateInfoType);

            FileGenericBase = TabAssembly.GetType("ZX.ZXFile`1", false);
            if (FileGenericBase != null)
            {
                Type closed = FileGenericBase.MakeGenericType(GameStateType);
                ZxFileReadMethod = closed.GetMethod(Unescape(N_ZXFILE_READ),
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (ZxFileReadMethod != null)
                    Found("ZXFile<ZXGameState> static read (path) [verify helper]", "exact-name+MakeGeneric", ZxFileReadMethod);
            }
            if (ZxFileReadMethod == null && ZipReadMethod == null)
                throw new Day0GenException("No read-back mechanism available (ZXFile reader and ZipSerializer.Read both missing)");

            if (requireEngineOnlyTargets)
            {
                // Nothing extra for now; placeholder for future engine-only targets.
            }
        }

        // Discovers the DXProject members probed (read-only) by
        // Program.LogProjectDiagnostics. Absence is never fatal: they only explain the
        // generator's DXProject.FromID NullReferenceException.
        private void DiscoverDxProjectDiagnosticMembers()
        {
            BindingFlags flags = BindingFlags.Static | BindingFlags.Public;

            try
            {
                DxProjectFromIdMethod = DxProjectType.GetMethod("FromID", flags);
            }
            catch (AmbiguousMatchException)
            {
                // More than one FromID overload: prefer the (ulong) -> DXProject one.
                BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (MethodInfo m in DxProjectType.GetMethods(all))
                {
                    if (m.Name != "FromID") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(ulong))
                    {
                        DxProjectFromIdMethod = m;
                        break;
                    }
                }
            }
            if (DxProjectFromIdMethod != null)
                Found("DXProject.FromID(ulong)", "public-stable-name", DxProjectFromIdMethod);
            else
                Log.Write("NOTE: DXProject.FromID not found; project diagnostics will skip the FromID probe.");

            DxProjectLoadedProjectsField = DxProjectType.GetField("LoadedProjects", flags);
            FoundOrNote("DXProject.LoadedProjects", DxProjectLoadedProjectsField);
            DxProjectLastLoadedField = DxProjectType.GetField("LastLoaded", flags);
            FoundOrNote("DXProject.LastLoaded", DxProjectLastLoadedField);
            DxProjectSGameFilesField = DxProjectType.GetField("SGameFiles", flags);
            FoundOrNote("DXProject.SGameFiles", DxProjectSGameFilesField);
            DxProjectSGameDirectoryField = DxProjectType.GetField("SGameDirectory", flags);
            FoundOrNote("DXProject.SGameDirectory", DxProjectSGameDirectoryField);
        }

        private void FoundOrNote(string purpose, FieldInfo f)
        {
            if (f != null) Found(purpose + " (static)", "public-stable-name", f);
            else Log.Write("NOTE: " + purpose + " field not found; project diagnostics will skip it.");
        }

        private MethodInfo FindGeneratorOn(Type t)
        {
            DxLevelType = DxAssembly.GetType("DXVision.DXLevel", false);
            if (DxLevelType == null) return null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != Unescape(N_GENERATE)) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == ParamsType && m.ReturnType == DxLevelType)
                    return m;
            }
            return null;
        }

        private MethodInfo ScanMethod(Type t, string what, Predicate<MethodInfo> pred)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                try { if (pred(m)) return m; } catch { }
            }
            return null;
        }

        internal static Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                Log.Write("WARNING: partial type load of " + asm.GetName().Name + " (" +
                          rtle.Types.Length + " loaded, " + rtle.LoaderExceptions.Length + " failed)");
                return rtle.Types;
            }
        }

        private void EnsureZipSerializer()
        {
            if (ZipSerializerType == null)
            {
                ZipSerializerType = DxAssembly.GetType("DXVision.Serialization.ZipSerializer", false);
                if (ZipSerializerType == null)
                    ZipSerializerType = TabAssembly.GetType("DXVision.Serialization.ZipSerializer", false);
            }
            if (ZipSerializerType == null)
                throw new Day0GenException("Type DXVision.Serialization.ZipSerializer not found");
        }

        // -----------------------------------------------------------------------
        // Logged invocation helpers
        // -----------------------------------------------------------------------
        private static string ArgsToString(object[] args)
        {
            if (args == null) return "<null>";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                object a = args[i];
                if (a == null) sb.Append("null");
                else if (a is string) sb.Append("\"" + a + "\"");
                else sb.Append(a.ToString());
            }
            return sb.ToString();
        }

        private static string ShortResult(object r)
        {
            if (r == null) return "null";
            string s = r.ToString();
            if (s.Length > 200) s = s.Substring(0, 200) + "...";
            return r.GetType().Name + "(" + s + ")";
        }

        public object Invoke(string purpose, MethodBase m, object target, params object[] args)
        {
            string targetDesc = (target == null ? "static" : target.GetType().Name) + " :: " + m.Name;
            Log.Write("INVOKE " + purpose + " [" + targetDesc + "] args=(" + ArgsToString(args) + ")");
            object result;
            try
            {
                if (m is ConstructorInfo)
                    result = ((ConstructorInfo)m).Invoke(args);
                else
                    result = m.Invoke(target, args);
            }
            catch (Exception e)
            {
                Exception root = e;
                if (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
                Log.Write("INVOKE FAILED " + purpose + ": " + root.GetType().Name + ": " + root.Message);
                if (root.StackTrace != null) Log.Write(root.StackTrace);
                LogInnerChain(root, "INVOKE FAILED " + purpose + " inner");
                throw new Day0GenException("Reflection invocation failed: " + purpose, root);
            }
            Log.Write("INVOKE OK " + purpose + " -> " + ShortResult(result));
            return result;
        }

        // Records every level of the inner-exception chain (type/message/stack). The
        // outermost TargetInvocationException is already unwrapped by the caller, so this
        // starts at root.InnerException.
        private static void LogInnerChain(Exception root, string label)
        {
            Exception cur = root.InnerException;
            int depth = 0;
            while (cur != null)
            {
                Log.Write(label + "[" + depth + "] " + cur.GetType().FullName + ": " + cur.Message);
                if (cur.StackTrace != null) Log.Write(label + "[" + depth + "] stack: " + cur.StackTrace);
                cur = cur.InnerException;
                depth++;
            }
        }

        // Constructor variant of Invoke: Activator wraps ctor exceptions in
        // TargetInvocationException, so unwrap to the real inner exception before
        // logging/throwing (otherwise the cause is lost).
        public object CreateInstance(string purpose, Type t, params object[] args)
        {
            Log.Write("INVOKE " + purpose + " [new " + t.Name + "] args=(" + ArgsToString(args) + ")");
            object result;
            try
            {
                result = Activator.CreateInstance(t, args);
            }
            catch (Exception e)
            {
                Exception root = e;
                if (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
                Log.Write("INVOKE FAILED " + purpose + ": " + root.GetType().Name + ": " + root.Message);
                if (root.StackTrace != null) Log.Write(root.StackTrace);
                throw new Day0GenException("Constructor invocation failed: " + purpose, root);
            }
            Log.Write("INVOKE OK " + purpose + " -> " + ShortResult(result));
            return result;
        }

        public object GetProp(string purpose, PropertyInfo p, object target)
        {
            Log.Write("GETPROP " + purpose + " [" + (target == null ? "static" : target.GetType().Name) + "." + p.Name + "]");
            object result;
            try { result = p.GetValue(target, null); }
            catch (Exception e)
            {
                Exception root = e;
                if (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
                Log.Write("GETPROP FAILED " + purpose + ": " + root.Message);
                throw new Day0GenException("Property get failed: " + purpose, root);
            }
            Log.Write("GETPROP OK " + purpose + " -> " + ShortResult(result));
            return result;
        }

        public void SetProp(string purpose, PropertyInfo p, object target, object value)
        {
            Log.Write("SETPROP " + purpose + " [" + (target == null ? "static" : target.GetType().Name) + "." + p.Name
                      + "] = " + (value == null ? "null" : ShortResult(value)));
            try { p.SetValue(target, value, null); }
            catch (Exception e)
            {
                Exception root = e;
                if (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
                Log.Write("SETPROP FAILED " + purpose + ": " + root.Message);
                throw new Day0GenException("Property set failed: " + purpose, root);
            }
            Log.Write("SETPROP OK " + purpose);
        }

        // Probes a (string,int,bool)->void candidate; returns true when it sets a
        // non-empty ZipSerializer.Current.Password (i.e. it is the setter).
        private bool ProbePasswordCandidate(MethodInfo cand)
        {
            try
            {
                int flag = (int)FlagMethod.Invoke(null, new object[] { @"C:\day0gen-probe.zxsav" });
                cand.Invoke(null, new object[] { @"C:\day0gen-probe.zxsav", flag, true });
                object zip = ZipCurrentProp.GetValue(null, null);
                if (zip == null) return false;
                string pwd = (string)ZipPasswordProp.GetValue(zip, null);
                return pwd != null && pwd.Length > 0;
            }
            catch (Exception e)
            {
                Log.Write("probe candidate " + cand.Name + " threw: " + e.Message);
                return false;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Program
    // ---------------------------------------------------------------------------
    internal static class Program
    {
        private static Options opts;
        private static GameReflector refl;
        private static volatile bool engineThreadDead;

        // Engine UI-thread marshal diagnostics (filled by FindEngineUiMarshalTarget,
        // logged right before Control.Invoke in RunFull).
        private static bool uiMarshalUsedFallback;
        private static IntPtr uiMarshalMainWindowHandle;
        private static int uiMarshalFormCount;

        // Stable id of the game's built-in project. The generator resolves its entity
        // templates through DXProject.FromID(this), so a non-null result is the real
        // prerequisite for generation (DXProject.Current alone becomes non-null too early,
        // part-way through engine init).
        private const ulong ProjectId = 7969835573169938409UL;

        // Command-center entity template id (the generator's first CreateInstance:
        // _0023_003DzYLpZK4K9zSHp._0023_003Dzyx9SpHmg_Pj8YUXUOQ_003D_003D._0023_003DzpMFG1rRDzbXO()
        // resolves into DXProject.EntityTemplates under this key). Used only by the
        // genprobe template-chain probe.
        private const ulong GenProbeCommandCenterTemplateId = 3153977018683405164UL;

        private static int Main(string[] args)
        {
            int code = RunMain(args);
            // The zombie engine (ZX.Program.Main) leaves foreground threads alive, so a
            // normal return can keep the process (and an SSH session) open. Force exit.
            Environment.Exit(code);
            return code; // unreachable: Environment.Exit terminates the process
        }

        private static int RunMain(string[] args)
        {
            Log.Open();

            // WHY: the engine's bootstrap hands off to Steam (Process.Start + Exit) when it
            // detects a non-Steam launch, which would kill this process before any reflection
            // happens. Declaring the TAB's Steam app identity makes it initialize in-process.
            Environment.SetEnvironmentVariable("SteamAppId", "644930");
            Environment.SetEnvironmentVariable("SteamGameId", "644930");
            Environment.SetEnvironmentVariable("SteamClientLaunch", "1");
            Log.Write("Steam env set: SteamAppId=644930, SteamGameId=644930, SteamClientLaunch=1 " +
                      "(bypass Steam relaunch handoff; engine initializes in-process).");

            int pid;
            using (Process selfProc = Process.GetCurrentProcess()) { pid = selfProc.Id; }
            Log.Write("=== Day0Gen starting (pid " + pid + ") ===");
            try
            {
                opts = Options.Parse(args);
            }
            catch (Day0GenException e)
            {
                Log.Write("CLI ERROR: " + e.Message);
                Console.Error.WriteLine(Usage());
                return 1;
            }
            Log.Write("Args: " + string.Join(" ", args));

            try
            {
                if (opts.Phase == "discovery") return RunDiscovery();
                if (opts.Phase == "zombie") return RunZombie();
                if (opts.Phase == "genprobe") return RunGenProbe();
                return RunFull();
            }
            catch (Day0GenException e)
            {
                Log.Write("ABORT: " + e.Message);
                if (e.InnerException != null)
                    Log.Write("  caused by: " + e.InnerException.GetType().Name + ": " + e.InnerException.Message);
                Log.Write("=== Day0Gen FAILED (phase " + opts.Phase + ") ===");
                return 1;
            }
            catch (Exception e)
            {
                Log.Write("ABORT (unexpected): full exception chain follows.");
                LogExceptionChain("ABORT", e);
                Log.Write("=== Day0Gen FAILED (phase " + opts.Phase + ") ===");
                return 1;
            }
        }

        private static void LogExceptionChain(string label, Exception e)
        {
            int depth = 0;
            Exception cur = e;
            while (cur != null)
            {
                Log.Write(label + "[" + depth + "] " + cur.GetType().FullName + ": " + cur.Message);
                if (cur.StackTrace != null) Log.Write(label + "[" + depth + "] stack: " + cur.StackTrace);
                cur = cur.InnerException;
                depth++;
            }
        }

        private static string Usage()
        {
            return "Day0Gen --phase discovery|zombie|genprobe|full [--tab-dir <dir>] [--saves-dir <dir>] [--seed N]\r\n"
                 + "        [--ncells N] [--duration F] [--pop F] [--name S] [--validate-signer <zxsav>]\r\n"
                 + "        (genprobe: interactive generator diagnostic; zombie init + engine-UI-thread\r\n"
                 + "         probe sequence; writes nothing besides Day0Gen.log)";
        }

        // ---------------------------------------------------------------------
        // Safety gates
        // ---------------------------------------------------------------------
        private static void RefuseIfGameRunning()
        {
            Process[] procs = Process.GetProcessesByName("TheyAreBillions");
            if (procs.Length > 0)
            {
                foreach (Process p in procs) { try { p.Dispose(); } catch { } }
                throw new Day0GenException("TheyAreBillions process is running. Refusing to start. Close the game first.");
            }
            Log.Write("Safety: no TheyAreBillions process running.");
        }

        private static void VerifyTabDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exe = Path.Combine(opts.TabDir, "TheyAreBillions.exe");
            if (!File.Exists(exe))
                throw new Day0GenException("TheyAreBillions.exe not found in TAB dir '" + opts.TabDir +
                                           "'. Run Day0Gen from the TAB install directory (or pass --tab-dir).");
            if (!File.Exists(Path.Combine(opts.TabDir, "DXVision.dll")))
                Log.Write("NOTE: no separate DXVision.dll; DXVision is an embedded assembly materialized by the TheyAreBillions module initializer.");
            Log.Write("TAB dir verified: " + opts.TabDir);
            if (string.Compare(Path.GetFullPath(baseDir).TrimEnd('\\'),
                               Path.GetFullPath(opts.TabDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) != 0)
            {
                Log.Write("WARNING: Day0Gen is not running from the TAB dir; installing AssemblyResolve hook to '" +
                          opts.TabDir + "'.");
                string dir = opts.TabDir;
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
                {
                    string simple = new AssemblyName(e.Name).Name;
                    string candidate = Path.Combine(dir, simple + ".dll");
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                    candidate = Path.Combine(dir, simple + ".exe");
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                    return null;
                };
            }
        }

        private static string RootDirOf(string savesDir)
        {
            DirectoryInfo di = new DirectoryInfo(Path.GetFullPath(savesDir));
            if (di.Parent == null) throw new Day0GenException("Cannot compute parent of " + savesDir);
            return di.Parent.FullName;
        }

        private static string ZxLogPath(string savesDir)
        {
            return Path.Combine(RootDirOf(savesDir), "ZXLog.txt");
        }

        private static void DumpZxLogTail(string savesDir, int lines)
        {
            string path = ZxLogPath(savesDir);
            try
            {
                if (!File.Exists(path))
                {
                    Log.Write("ZXLog not present at " + path);
                    return;
                }
                List<string> all = new List<string>(File.ReadAllLines(path));
                int start = Math.Max(0, all.Count - lines);
                Log.Write("--- ZXLog tail (" + path + ") ---");
                for (int i = start; i < all.Count; i++) Log.Write("  | " + all[i]);
                Log.Write("--- end ZXLog tail ---");
            }
            catch (Exception e)
            {
                Log.Write("Cannot read ZXLog: " + e.Message);
            }
        }

        // ---------------------------------------------------------------------
        // Phase: discovery (no zombie, no writes)
        // ---------------------------------------------------------------------
        private static int RunDiscovery()
        {
            RefuseIfGameRunning();
            VerifyTabDir();
            refl = new GameReflector();
            refl.LoadAssemblies();
            refl.DiscoverAll(false);
            Log.Write("PHASE discovery COMPLETE. No engine init, no writes performed.");
            return 0;
        }

        // ---------------------------------------------------------------------
        // Zombie engine init (TABSAT pattern)
        // ---------------------------------------------------------------------
        private static object managerInstance;

        private static void ZombieInit()
        {
            // Empty args = a normal launch: the engine runs its own startup in-process
            // and reaches the main menu, which is what populates DXProject.Current.
            // TABSAT passes new string[] { "" }; that empty-string arg makes the engine
            // settle at its args-error modal and stall there, leaving DXProject.Current
            // unset. Real (non-empty) args made the bootstrap treat this as a normal
            // Steam launch and hand off (Process.Start + Environment.Exit), killing this
            // process before construction; that handoff is now bypassed by the Steam env
            // vars set before Main is invoked.
            Thread t = new Thread(delegate()
            {
                try
                {
                    Log.Write("Engine thread: invoking ZX.Program.Main ...");
                    refl.MainMethod.Invoke(null, new object[] { new string[0] });
                    Log.Write("Engine thread: Main returned.");
                }
                catch (Exception e)
                {
                    Exception rootEx = e;
                    if (rootEx is TargetInvocationException && rootEx.InnerException != null) rootEx = rootEx.InnerException;
                    Log.Write("Engine thread: Main threw: " + rootEx.GetType().Name + ": " + rootEx.Message);
                }
                finally
                {
                    engineThreadDead = true;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();

            // Poll for the manager singleton. We no longer minimize any window: with the
            // normal-launch (empty) args there is no error popup, so the first window is the
            // real game window, and minimizing it while it creates its D3D device caused
            // intermittent D3DERR_INVALIDCALL in DXRender_SlimDX.D3DCreateDevice.
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                object mgr = null;
                try
                {
                    mgr = refl.ManagerCurrentMethod.Invoke(null, null);
                }
                catch
                {
                    // engine not ready yet
                }
                if (mgr != null)
                {
                    managerInstance = mgr;
                    Log.Write("Manager singleton ready: " + mgr.GetType().Name);
                    break;
                }
                Thread.Sleep(250);
            }

            if (managerInstance == null)
            {
                Log.Write("TIMEOUT: engine manager singleton not ready after 60s.");
                DumpZxLogTail(EffectiveSavesDir(), 60);
                throw new Day0GenException("Zombie engine init timed out (manager singleton null). See ZXLog tail above.");
            }

            // Account check: only read the GameAccount property once we know the
            // account file exists - the getter would otherwise CREATE and save a
            // fresh account, which we must never do.
            string root = RootDirOf(EffectiveSavesDir());
            string accountFile = Path.Combine(root, "Account.zxuser");
            if (!File.Exists(accountFile))
                throw new Day0GenException("Account.zxuser not found at " + accountFile +
                                           " - refusing to touch GameAccount (getter would create a new account file).");
            object account = refl.GetProp("manager.GameAccount", refl.GameAccountProp, managerInstance);
            if (account == null)
                throw new Day0GenException("GameAccount is null even though " + accountFile + " exists.");
            Log.Write("GameAccount readable: " + account.GetType().Name);

            CheckThemeTable();
        }

        private static int ThemeCount(object table)
        {
            System.Collections.ICollection c = table as System.Collections.ICollection;
            return c == null ? 0 : c.Count;
        }

        // Reads the theme table (cheap) and reports whether it is non-empty. A read
        // that throws is treated as "not ready" so callers can keep waiting.
        private static bool TryReadThemeTable(string purpose, ref object table)
        {
            table = null;
            try
            {
                table = refl.Invoke(purpose, refl.ThemeTableMethod, null);
            }
            catch (Day0GenException e)
            {
                Log.Write("Theme table read threw: " + e.Message);
                return false;
            }
            return ThemeCount(table) > 0;
        }

        private static void CheckThemeTable()
        {
            // First attempt: the theme table may already be populated.
            object table = null;
            if (TryReadThemeTable("ZXMapTheme theme table", ref table))
            {
                Log.Write("Theme table OK (" + ThemeCount(table) + " themes).");
                return;
            }

            // Passive wait for the engine's OWN table load. The engine's Main is still
            // running its "Tables Excel Read" inside the manager ctor; invoking the
            // loader concurrently corrupts the shared static tables (manager ctor
            // NREs), so TableLoadMethod must NOT be called while the engine thread is
            // alive. Poll the table (cheap, at most once per second) and stop as soon
            // as the engine thread dies.
            Log.Write("Theme table empty; waiting for engine table load (up to 90s) ...");
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            DateTime lastRead = DateTime.UtcNow;   // the first read just happened
            DateTime lastProgress = DateTime.UtcNow;
            bool engineDied = engineThreadDead;
            while (DateTime.UtcNow < deadline && !engineDied)
            {
                Thread.Sleep(500);
                engineDied = engineThreadDead;
                if (DateTime.UtcNow - lastRead >= TimeSpan.FromSeconds(1))
                {
                    lastRead = DateTime.UtcNow;
                    if (TryReadThemeTable("ZXMapTheme theme table (poll)", ref table))
                    {
                        Log.Write("Theme table OK after engine-init wait (" + ThemeCount(table) + " themes).");
                        return;
                    }
                }
                if (DateTime.UtcNow - lastProgress >= TimeSpan.FromSeconds(10))
                {
                    lastProgress = DateTime.UtcNow;
                    int left = (int)(deadline - DateTime.UtcNow).TotalSeconds;
                    if (left < 0) left = 0;
                    Log.Write("Theme table still empty (engine " + (engineThreadDead ? "dead" : "alive") +
                              ", ~" + left + "s left) ...");
                }
            }

            // The wait may have ended with the table populated (engine finished init or
            // died after loading it) - re-check before any fallback.
            if (TryReadThemeTable("ZXMapTheme theme table (post-wait)", ref table))
            {
                Log.Write("Theme table OK (" + ThemeCount(table) + " themes).");
                return;
            }

            // Last resort: only once the engine thread is gone. The headless
            // `--phase zombie` path always ends with the engine dead, so this preserves
            // it; invoking the loader while the engine is alive would race its own load.
            if (engineThreadDead && refl.TableLoadMethod != null)
            {
                Log.Write("Engine thread died with theme table empty; invoking engine table loader (last resort) ...");
                try
                {
                    refl.Invoke("engine table loader", refl.TableLoadMethod, null);
                }
                catch (Day0GenException e)
                {
                    Log.Write("Engine table loader threw: " + e.Message);
                }
                Thread.Sleep(3000);
                if (TryReadThemeTable("ZXMapTheme theme table (loader retry)", ref table))
                {
                    Log.Write("Theme table OK after loader fallback (" + ThemeCount(table) + " themes).");
                    return;
                }
            }
            else
            {
                Log.Write("Theme table timeout with engine thread still alive; not invoking loader " +
                          "(would race engine init).");
            }

            throw new Day0GenException("Theme table unavailable after loader attempt - map generation requires it.");
        }

        private static string EffectiveSavesDir()
        {
            if (opts.SavesDirExplicit) return opts.SavesDir;
            if (managerInstance != null && refl != null && refl.SavesFolderMethod != null)
            {
                try
                {
                    object dir = refl.SavesFolderMethod.IsStatic
                        ? refl.Invoke("manager saves folder (static)", refl.SavesFolderMethod, null)
                        : refl.Invoke("manager saves folder (instance)", refl.SavesFolderMethod, managerInstance);
                    string s = dir as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch (Day0GenException e)
                {
                    Log.Write("WARNING: manager saves-folder lookup failed (" + e.Message + "); using default.");
                }
            }
            return opts.SavesDir != null ? opts.SavesDir : opts.DefaultSavesDir();
        }

        // ---------------------------------------------------------------------
        // Password machinery probe (read-only; dummy path must NOT be created)
        // ---------------------------------------------------------------------
        private static void ProbePasswordMachinery()
        {
            string dummy = Path.Combine(EffectiveSavesDir(), "day0gen-probe.dummy");
            Log.Write("Password probe on dummy path (file must NOT be created): " + dummy);
            bool dummyWorks = TryPasswordDerivation(dummy);
            if (File.Exists(dummy))
                throw new Day0GenException("SAFETY VIOLATION: dummy probe path was created by engine code!");
            if (dummyWorks)
            {
                Log.Write("Password machinery works on dummy path.");
                return;
            }
            Log.Write("Dummy-path probe failed (expected on some engine paths); retrying read-only against an existing save ...");
            string[] existing = Directory.Exists(EffectiveSavesDir())
                ? Directory.GetFiles(EffectiveSavesDir(), "*.zxsav")
                : new string[0];
            if (existing.Length == 0)
                throw new Day0GenException("No existing .zxsav to verify password machinery; aborting.");
            bool realWorks = TryPasswordDerivation(existing[0]);
            if (!realWorks)
                throw new Day0GenException("Password derivation failed on existing save " + existing[0] + "; aborting.");
            Log.Write("Password machinery verified against " + existing[0]);
        }

        private static bool TryPasswordDerivation(string path)
        {
            try
            {
                object flagObj = refl.Invoke("password flag", refl.FlagMethod, null, path);
                int flag = (int)flagObj;
                refl.Invoke("password generator(set)", refl.PwdSetMethod, null, path, flag, true);
                object zip = refl.GetProp("ZipSerializer.Current", refl.ZipCurrentProp, null);
                if (zip == null)
                {
                    Log.Write("ZipSerializer.Current is null after generator invocation.");
                    return false;
                }
                string pwd = (string)refl.GetProp("ZipSerializer.Password", refl.ZipPasswordProp, zip);
                if (string.IsNullOrEmpty(pwd))
                {
                    Log.Write("ZipSerializer.Password empty after generator invocation.");
                    return false;
                }
                Log.Write("Password derived (len " + pwd.Length + ") for " + path);
                // clear the password state again (read-only hygiene)
                if (refl.PwdClearMethod != null)
                {
                    try { refl.Invoke("password generator(clear)", refl.PwdClearMethod, null, path, flag, true); }
                    catch (Day0GenException e) { Log.Write("password clear failed (non-fatal): " + e.Message); }
                }
                return true;
            }
            catch (Day0GenException e)
            {
                Log.Write("Password derivation on " + path + " failed: " + e.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // Wait for the engine's project context to be fully initialized.
        //
        // DXProject.Current becomes non-null part-way through engine init, so it is NOT
        // a sufficient readiness signal: the generator later resolves entity templates via
        // DXProject.FromID(ProjectId), which only succeeds once init has progressed further.
        // Gate on FromID returning non-null; use Current for diagnosis only.
        // ---------------------------------------------------------------------
        private static void WaitForProjectContext()
        {
            bool useFromId = refl.DxProjectFromIdMethod != null;
            if (!useFromId &&
                (refl.DxProjectType == null ||
                 (refl.DxProjectCurrentProp == null && refl.DxProjectCurrentField == null)))
                throw new Day0GenException("Neither DXProject.FromID nor DXProject.Current was discovered; " +
                                           "cannot verify engine project init before construction.");

            if (useFromId)
            {
                Log.Write("Waiting for DXProject.FromID(" + ProjectId + ") != null (engine project-context " +
                          "readiness; DXProject.Current alone is not sufficient) ...");
            }
            else
            {
                Log.Write("WARNING: DXProject.FromID was not discovered; falling back to the WEAKER readiness " +
                          "signal DXProject.Current != null (it can become non-null before project init completes).");
                Log.Write("Waiting for DXProject.Current (engine scene/project context) ...");
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            int polls = 0;
            while (DateTime.UtcNow < deadline && !engineThreadDead)
            {
                object ready = null;
                string signal = useFromId ? "DXProject.FromID" : "DXProject.Current";
                try
                {
                    if (useFromId)
                        ready = refl.DxProjectFromIdMethod.Invoke(null, new object[] { ProjectId });
                    else
                        ready = ReadDxProjectCurrent();
                }
                catch (Exception e)
                {
                    Exception root = e;
                    if (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
                    if (polls == 0)
                        Log.Write(signal + " probe threw (will keep polling): " +
                                  root.GetType().Name + ": " + root.Message);
                }
                polls++;
                if (ready != null)
                {
                    Log.Write(signal + " ready after " + polls + " poll(s): " + ready.GetType().FullName);
                    LogCurrentProjectContextForDiagnosis();
                    return;
                }
                if (polls % 20 == 0)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalSeconds;
                    if (left < 0) left = 0;
                    Log.Write(signal + " still null (poll " + polls + ", ~" + left + "s left) ...");
                }
                Thread.Sleep(500);
            }

            if (engineThreadDead)
                Log.Write("Engine thread died before project context became ready; stopping early.");
            Log.Write("TIMEOUT: project context (DXProject.FromID/Current) remained null for ~90s; " +
                      "engine project init did not complete.");
            DumpZxLogTail(EffectiveSavesDir(), 60);
            throw new Day0GenException("Engine project init did not complete: project context was null " +
                                       "after ~90s. See ZXLog tail above.");
        }

        // Read-only, log-only probe of DXProject.Current alongside the FromID readiness
        // gate above. Never affects control flow.
        private static void LogCurrentProjectContextForDiagnosis()
        {
            if (refl.DxProjectCurrentProp == null && refl.DxProjectCurrentField == null)
            {
                Log.Write("DXPROJECT (diagnostic) DXProject.Current: member not discovered");
                return;
            }
            try
            {
                object current = ReadDxProjectCurrent();
                Log.Write("DXPROJECT (diagnostic) DXProject.Current: " +
                          (current == null ? "null" : current.GetType().FullName));
            }
            catch (Exception e)
            {
                Log.Write("DXPROJECT (diagnostic) DXProject.Current read FAILED: " + DescribeException(e));
            }
        }

        // ---------------------------------------------------------------------
        // DXProject diagnostics: read-only probe logged immediately before
        // generation. The generator NREs at DXProject.FromID(...).EntityTemplates[...];
        // these reads tell whether FromID returned null or the template map is
        // null/empty. Never aborts.
        // ---------------------------------------------------------------------
        private static void LogProjectDiagnostics()
        {
            Log.Write("--- DXProject diagnostics (read-only, pre-generation) ---");

            try
            {
                object current = ReadDxProjectCurrent();
                Log.Write("DXPROJECT DXProject.Current: " +
                          (current == null ? "null" : current.GetType().FullName));
            }
            catch (Exception e)
            {
                Log.Write("DXPROJECT DXProject.Current read FAILED: " + DescribeException(e));
            }

            try
            {
                FieldInfo f = refl.DxProjectLoadedProjectsField;
                if (f == null)
                {
                    Log.Write("DXPROJECT DXProject.LoadedProjects: member not discovered");
                }
                else
                {
                    object loaded = f.GetValue(null);
                    Log.Write("DXPROJECT DXProject.LoadedProjects: " +
                              (loaded == null ? "null" : loaded.GetType().FullName));
                    System.Collections.IDictionary dict = loaded as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        Log.Write("DXPROJECT DXProject.LoadedProjects Count=" + dict.Count);
                        foreach (object key in dict.Keys)
                            Log.Write("DXPROJECT LoadedProjects key: " + (key == null ? "null" : key.ToString()));
                    }
                    else if (loaded is System.Collections.ICollection)
                    {
                        Log.Write("DXPROJECT DXProject.LoadedProjects Count=" +
                                  ((System.Collections.ICollection)loaded).Count +
                                  " (not IDictionary; keys unavailable)");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("DXPROJECT DXProject.LoadedProjects read FAILED: " + DescribeException(e));
            }

            LogDxProjectField("DXProject.LastLoaded", refl.DxProjectLastLoadedField);
            LogDxProjectField("DXProject.SGameFiles", refl.DxProjectSGameFilesField);
            LogDxProjectField("DXProject.SGameDirectory", refl.DxProjectSGameDirectoryField);

            try
            {
                MethodInfo fromId = refl.DxProjectFromIdMethod;
                if (fromId == null)
                {
                    Log.Write("DXPROJECT DXProject.FromID: member not discovered");
                }
                else
                {
                    object project = fromId.Invoke(null, new object[] { ProjectId });
                    Log.Write("DXPROJECT DXProject.FromID(" + ProjectId + "): " +
                              (project == null ? "null" : project.GetType().FullName));
                    if (project != null) LogDxProjectEntityTemplates(project);
                }
            }
            catch (Exception e)
            {
                Log.Write("DXPROJECT DXProject.FromID probe FAILED: " + DescribeException(e));
            }

            Log.Write("--- end DXProject diagnostics ---");
        }

        private static void LogDxProjectField(string label, FieldInfo f)
        {
            try
            {
                if (f == null)
                {
                    Log.Write("DXPROJECT " + label + ": member not discovered");
                    return;
                }
                object v = f.GetValue(null);
                Log.Write("DXPROJECT " + label + ": " + DescribeValue(v));
            }
            catch (Exception e)
            {
                Log.Write("DXPROJECT " + label + " read FAILED: " + DescribeException(e));
            }
        }

        private static void LogDxProjectEntityTemplates(object project)
        {
            try
            {
                Type t = project.GetType();
                object templates = null;
                PropertyInfo p = t.GetProperty("EntityTemplates",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null)
                {
                    templates = p.GetValue(project, null);
                }
                else
                {
                    FieldInfo f = t.GetField("EntityTemplates",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null)
                    {
                        Log.Write("DXPROJECT EntityTemplates: member not found on " + t.FullName);
                        return;
                    }
                    templates = f.GetValue(project);
                }
                if (templates == null)
                {
                    Log.Write("DXPROJECT EntityTemplates: null");
                    return;
                }
                System.Collections.ICollection col = templates as System.Collections.ICollection;
                Log.Write("DXPROJECT EntityTemplates: " + templates.GetType().FullName +
                          (col == null ? "" : " Count=" + col.Count));
            }
            catch (Exception e)
            {
                Log.Write("DXPROJECT EntityTemplates read FAILED: " + DescribeException(e));
            }
        }

        private static object ReadDxProjectCurrent()
        {
            if (refl.DxProjectCurrentProp != null)
                return refl.DxProjectCurrentProp.GetValue(null, null);
            if (refl.DxProjectCurrentField != null)
                return refl.DxProjectCurrentField.GetValue(null);
            return null;
        }

        private static string DescribeValue(object v)
        {
            if (v == null) return "null";
            if (v is string) return "string(\"" + v + "\")";
            return v.GetType().FullName + "(" + v + ")";
        }

        private static string DescribeException(Exception e)
        {
            Exception root = e;
            if (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
            return root.GetType().Name + ": " + root.Message;
        }

        // ---------------------------------------------------------------------
        // Signer validation: signature(file) must equal sibling .zxcheck content
        // ---------------------------------------------------------------------
        private static void ValidateSigner(string zxsavPath)
        {
            if (!File.Exists(zxsavPath))
                throw new Day0GenException("--validate-signer: file not found: " + zxsavPath);
            string checkPath = Path.ChangeExtension(zxsavPath, ".zxcheck");
            if (!File.Exists(checkPath))
                throw new Day0GenException("--validate-signer: sibling .zxcheck not found: " + checkPath);
            string sig = (string)refl.Invoke("signing(file,2)", refl.SigningMethod, null, zxsavPath, 2);
            string expected = File.ReadAllText(checkPath).Trim();
            Log.Write("Signer produced : " + sig);
            Log.Write(".zxcheck content: " + expected);
            if (sig != expected)
                throw new Day0GenException("Signer validation FAILED: signature does not match .zxcheck content.");
            Log.Write("Signer validation PASSED.");
        }

        // ---------------------------------------------------------------------
        // Phase: zombie
        // ---------------------------------------------------------------------
        private static int RunZombie()
        {
            RefuseIfGameRunning();
            VerifyTabDir();
            refl = new GameReflector();
            refl.LoadAssemblies();
            refl.DiscoverAll(false);
            ZombieInit();
            ProbePasswordMachinery();
            if (opts.ValidateSigner != null) ValidateSigner(opts.ValidateSigner);
            Log.Write("PHASE zombie COMPLETE. Engine initialized, account + password machinery verified. No save writes.");
            return 0;
        }

        // ---------------------------------------------------------------------
        // Phase: genprobe - runtime diagnostic for the generator NRE.
        //
        // The generator #=zEzgd90E=(ZXRandomLevelParams) dies ~120ms in with a
        // STACKLESS NullReferenceException right after logging "Random Map Creation
        // with seed". Every decompile-visible static checks out non-null, but the
        // heavy lifting (DXWorldGrid / DXNoyseLayer / ZXMapDrawer / DXRandom weighted
        // choice) lives in the embedded DXVision assembly, which cannot be
        // decompiled locally - so the failure is localized empirically: a fixed probe
        // sequence runs on the engine UI thread exactly like phase full, each step
        // logging PASS/FAIL with its exception chain, and a FirstChanceException
        // handler (registered before engine start) catches the NRE at throw time,
        // when the CLR may still have a stack for it.
        //
        // Read-only phase: no directory snapshots, no save writes; the ONLY file this
        // tool writes is Day0Gen.log (the engine keeps appending its own ZXLog.txt,
        // same documented deviation as phase zombie).
        // ---------------------------------------------------------------------
        private static int RunGenProbe()
        {
            RefuseIfGameRunning();
            VerifyTabDir();

            // Early on the main thread, before the engine thread exists: FirstChanceException
            // must be subscribed before the engine starts throwing.
            RegisterFirstChanceHandler();

            refl = new GameReflector();
            refl.LoadAssemblies();
            refl.DiscoverAll(false);

            ZombieInit();               // includes account + theme table gates
            ProbePasswordMachinery();
            WaitForProjectContext();

            Log.Write("GENPROBE: starting probe sequence on the engine UI thread; this phase writes " +
                      "nothing besides Day0Gen.log (no snapshots, no save artifacts).");
            try
            {
                RunOnEngineUiThread("genprobe sequence",
                    "no artifacts are written by the genprobe phase; just collect Day0Gen.log.",
                    delegate { GenProbeSequence(); });
            }
            finally
            {
                UnregisterFirstChanceHandler();
                DumpZxLogTail(EffectiveSavesDir(), 25);
            }
            Log.Write("GENPROBE DONE");
            Log.Write("=== Day0Gen OK (genprobe diagnostic sequence completed) ===");
            return 0;
        }

        private static void GenProbeSequence()
        {
            Log.Write("GENPROBE: sequence starting on engine UI thread (thread id=" +
                      Thread.CurrentThread.ManagedThreadId + ").");
            ProbeStep("step1 theme pick (DXRandom + ChooseValueWithWeights)", delegate { GenProbeStep1Theme(); });
            ProbeStep("step1b exact generator theme pick (instance ChooseValueWithWeights)",
                     delegate { GenProbeStep1bThemeExact(); });
            ProbeStep("step2 DXWorldGrid.Create", delegate { GenProbeStep2WorldGridCreate(); });
            ProbeStep("step3 DXWorldGrid.GetSceneSquareArea", delegate { GenProbeStep3SceneSquareArea(); });
            ProbeStep("step4 DXWorldGrid.ScenePointFromWorldCell", delegate { GenProbeStep4ScenePointFromWorldCell(); });
            ProbeStep("step5 DXNoyseLayer op chain", delegate { GenProbeStep5NoyseLayer(); });
            ProbeStep("step6 ZXMapDrawer layers", delegate { GenProbeStep6MapDrawer(); });
            ProbeStep("step7 entity template chain", delegate { GenProbeStep7TemplateChain(); });
            ProbeStep("step8 generator attempts", delegate { GenProbeStep8Generator(); });
        }

        // Wraps one probe step: PASS/FAIL + full exception chain; never aborts the
        // remaining sequence (a FAIL is the diagnostic payload, not a stop signal).
        private static void ProbeStep(string label, MethodInvoker op)
        {
            Log.Write("GENPROBE " + label + ": start");
            try
            {
                op();
                Log.Write("GENPROBE " + label + ": PASS");
            }
            catch (Exception e)
            {
                Log.Write("GENPROBE " + label + ": FAIL");
                LogExceptionChain("GENPROBE " + label, e);
            }
        }

        // Sub-operation inside a step (step5 chain): same logging, but returns
        // success instead of throwing so later sub-operations still run.
        private static bool ProbeOp(string label, MethodInvoker op)
        {
            try
            {
                op();
                Log.Write("GENPROBE op " + label + ": PASS");
                return true;
            }
            catch (Exception e)
            {
                Log.Write("GENPROBE op " + label + ": FAIL");
                LogExceptionChain("GENPROBE op " + label, e);
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // FirstChanceException diagnostic. The generator NRE is stackless by the
        // time our catch block sees it; at throw time the CLR may still have frames.
        // Filter: NullReferenceException / KeyNotFoundException / IndexOutOfRangeException
        // whose stack mentions the generator class or the DXVision map types; stackless
        // exceptions of those types are logged too (marked) - the stackless NRE is
        // precisely the failure under investigation. Tiny + fully guarded: this runs
        // on EVERY first-chance exception in the process until unregistered.
        // ---------------------------------------------------------------------
        private static EventHandler<FirstChanceExceptionEventArgs> genProbeFce;
        private static int genProbeFceLogged;
        private const int GenProbeFceLogLimit = 150;

        private static void RegisterFirstChanceHandler()
        {
            genProbeFceLogged = 0;
            genProbeFce = delegate(object sender, FirstChanceExceptionEventArgs e)
            {
                try
                {
                    if (genProbeFceLogged >= GenProbeFceLogLimit) return;
                    Exception ex = e.Exception;
                    if (ex == null) return;
                    string tn = ex.GetType().Name;
                    if (tn != "NullReferenceException" && tn != "KeyNotFoundException"
                        && tn != "IndexOutOfRangeException") return;
                    string st = ex.StackTrace;
                    if (st == null || st.Length == 0)
                    {
                        genProbeFceLogged++;
                        Log.Write("FCE: " + tn + ": " + ex.Message + " -- NO STACK (stackless)");
                        LogFceDiagnostics(ex);
                        return;
                    }
                    if (st.IndexOf("zyl_NPjjlA7DRfVtsRJCX1kN4BxSr", StringComparison.Ordinal) < 0
                        && st.IndexOf("DXNoyseLayer", StringComparison.Ordinal) < 0
                        && st.IndexOf("DXWorldGrid", StringComparison.Ordinal) < 0
                        && st.IndexOf("ZXMapDrawer", StringComparison.Ordinal) < 0)
                        return;
                    genProbeFceLogged++;
                    Log.Write("FCE: " + tn + ": " + ex.Message);
                    Log.Write("FCE: stack: " + st);
                    LogFceDiagnostics(ex);
                }
                catch { }
            };
            AppDomain.CurrentDomain.FirstChanceException += genProbeFce;
            Log.Write("FCE handler registered (first-chance NRE/KeyNotFound/IndexOutOfRange filter, " +
                      "generator + DXVision map-type stacks).");
        }

        // Second half of the FCE diagnostic (run for EVERY filtered exception,
        // stackless or not): StackTrace(ex, true) is captured at throw time, when
        // the runtime has already recorded the frames even though ex.StackTrace
        // (the formatted string) is still empty - so the frame list with IL/native
        // offsets is the decisive localization for the stackless generator NRE.
        // GetILOffset() returns -1 (prints as 0xFFFFFFFF) for inlined/missing info;
        // log whatever comes. TargetSite/Source/HResult follow. Every read is
        // individually guarded: this runs inside the first-chance callback.
        private static void LogFceDiagnostics(Exception ex)
        {
            try
            {
                StackTrace trace = new StackTrace(ex, true);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    try
                    {
                        StackFrame f = trace.GetFrame(i);
                        if (f == null)
                        {
                            Log.Write("FCE frame[" + i + "]: <null frame>");
                            continue;
                        }
                        MethodBase m = null;
                        try { m = f.GetMethod(); } catch { }
                        string owner = "?";
                        string mname = "?";
                        if (m != null)
                        {
                            mname = m.Name;
                            if (m.DeclaringType != null)
                                owner = m.DeclaringType.FullName ?? m.DeclaringType.Name;
                        }
                        Log.Write("FCE frame: " + owner + "." + mname +
                                  " IL=0x" + f.GetILOffset().ToString("X") +
                                  " native=0x" + f.GetNativeOffset().ToString("X") +
                                  " file=" + f.GetFileLineNumber());
                    }
                    catch (Exception fe)
                    {
                        Log.Write("FCE frame[" + i + "]: <read failed: " + fe.GetType().Name + ">");
                    }
                }
                if (trace.FrameCount == 0)
                    Log.Write("FCE: StackTrace(ex,true) captured 0 frame(s).");
            }
            catch (Exception e)
            {
                Log.Write("FCE: StackTrace(ex,true) build failed: " + e.GetType().Name + ": " + e.Message);
            }
            try
            {
                MethodBase ts = null;
                try { ts = ex.TargetSite; } catch { }
                if (ts == null)
                {
                    Log.Write("FCE TargetSite: null");
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("FCE TargetSite: ").Append(ts.Name);
                    try { sb.Append(" MetadataToken=0x").Append(ts.MetadataToken.ToString("X")); }
                    catch (Exception mte) { sb.Append(" MetadataToken=<").Append(mte.GetType().Name).Append(">"); }
                    try
                    {
                        if (ts.DeclaringType != null)
                            sb.Append(" DeclaringType=").Append(ts.DeclaringType.FullName ?? ts.DeclaringType.Name);
                        else sb.Append(" DeclaringType=null");
                    }
                    catch (Exception dte) { sb.Append(" DeclaringType=<").Append(dte.GetType().Name).Append(">"); }
                    Log.Write(sb.ToString());
                }
            }
            catch (Exception e)
            {
                Log.Write("FCE TargetSite: <read failed: " + e.GetType().Name + ">");
            }
            try { Log.Write("FCE Source: " + (ex.Source ?? "null")); }
            catch (Exception e) { Log.Write("FCE Source: <read failed: " + e.GetType().Name + ">"); }
            try { Log.Write("FCE HResult: 0x" + ex.HResult.ToString("X")); }
            catch (Exception e) { Log.Write("FCE HResult: <read failed: " + e.GetType().Name + ">"); }
        }

        private static void UnregisterFirstChanceHandler()
        {
            if (genProbeFce != null)
            {
                AppDomain.CurrentDomain.FirstChanceException -= genProbeFce;
                genProbeFce = null;
                Log.Write("FCE handler unregistered (" + genProbeFceLogged + " event(s) logged, cap " +
                          GenProbeFceLogLimit + ").");
            }
        }

        // ---------------------------------------------------------------------
        // Probe steps. Types resolve via DxAssembly.GetType with TabAssembly
        // fallback (or the reverse for ZX.* types); all method signatures are
        // DISCOVERED at runtime and logged (DXVision is not decompilable locally,
        // so the log becomes the signature catalog), and arguments are adapted to
        // the discovered parameter types - never assumed.
        // ---------------------------------------------------------------------
        private static Type FindTypeAnyOrder(string fullName, Assembly first)
        {
            Type t = null;
            try { t = first.GetType(fullName, false); } catch { }
            if (t == null)
            {
                Assembly other = (first == refl.DxAssembly) ? refl.TabAssembly : refl.DxAssembly;
                try { t = other.GetType(fullName, false); } catch { }
            }
            return t;
        }

        private static PropertyInfo FindPropertyUp(Type t, string name)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                PropertyInfo p = null;
                try
                {
                    p = cur.GetProperty(name, BindingFlags.Instance | BindingFlags.Static |
                                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (p != null) return p;
            }
            return null;
        }

        private static FieldInfo FindFieldUp(Type t, string name)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                FieldInfo f = null;
                try
                {
                    f = cur.GetField(name, BindingFlags.Instance | BindingFlags.Static |
                                           BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (f != null) return f;
            }
            return null;
        }

        private static string DescribeMethod(MethodBase m)
        {
            ParameterInfo[] ps = m.GetParameters();
            StringBuilder sb = new StringBuilder();
            MethodInfo mi = m as MethodInfo;
            if (mi != null) sb.Append(mi.ReturnType.Name).Append(" ");
            sb.Append(m.DeclaringType != null ? m.DeclaringType.FullName : "?");
            sb.Append("::").Append(m.Name).Append("(");
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.FullName != null ? ps[i].ParameterType.FullName : ps[i].ParameterType.Name);
            }
            sb.Append(")");
            return sb.ToString();
        }

        // Adapts one boxed argument to a discovered parameter type (numeric width,
        // enums, Point/PointF and Rectangle/RectangleF). Throws when no conversion
        // exists, which callers treat as "overload does not fit - try the next".
        private static object ConvertArg(object v, Type target)
        {
            Type tgt = target;
            if (tgt.IsGenericType && tgt.GetGenericTypeDefinition() == typeof(Nullable<>))
                tgt = tgt.GetGenericArguments()[0];
            if (v == null) return null;
            if (tgt.IsInstanceOfType(v)) return v;
            if (tgt == typeof(int)) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            if (tgt == typeof(long)) return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            if (tgt == typeof(short)) return Convert.ToInt16(v, CultureInfo.InvariantCulture);
            if (tgt == typeof(byte)) return Convert.ToByte(v, CultureInfo.InvariantCulture);
            if (tgt == typeof(uint)) return Convert.ToUInt32(v, CultureInfo.InvariantCulture);
            if (tgt == typeof(ulong)) return Convert.ToUInt64(v, CultureInfo.InvariantCulture);
            if (tgt == typeof(float)) return Convert.ToSingle(v, CultureInfo.InvariantCulture);
            if (tgt == typeof(double)) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (tgt == typeof(bool)) return Convert.ToBoolean(v, CultureInfo.InvariantCulture);
            if (tgt.IsEnum) return Enum.ToObject(tgt, v);
            if (tgt == typeof(System.Drawing.Point))
            {
                if (v is System.Drawing.PointF)
                {
                    System.Drawing.PointF pf = (System.Drawing.PointF)v;
                    return new System.Drawing.Point((int)pf.X, (int)pf.Y);
                }
            }
            if (tgt == typeof(System.Drawing.PointF))
            {
                if (v is System.Drawing.Point)
                    return (System.Drawing.PointF)(System.Drawing.Point)v;
            }
            if (tgt == typeof(System.Drawing.Rectangle))
            {
                if (v is System.Drawing.RectangleF)
                {
                    System.Drawing.RectangleF rf = (System.Drawing.RectangleF)v;
                    return new System.Drawing.Rectangle((int)rf.X, (int)rf.Y, (int)rf.Width, (int)rf.Height);
                }
            }
            if (tgt == typeof(System.Drawing.RectangleF))
            {
                if (v is System.Drawing.Rectangle)
                    return (System.Drawing.RectangleF)(System.Drawing.Rectangle)v;
            }
            throw new Day0GenException("cannot adapt " + v.GetType().Name + " to " + tgt.Name);
        }

        private static object[] AdaptArgs(string purpose, ParameterInfo[] ps, object[] args)
        {
            object[] adapted = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                try { adapted[i] = ConvertArg(args[i], ps[i].ParameterType); }
                catch (Exception e)
                {
                    throw new Day0GenException(purpose + ": cannot adapt arg " + i + " (" +
                        (args[i] == null ? "null" : args[i].GetType().Name) + " -> " +
                        ps[i].ParameterType.Name + "): " + e.Message, e);
                }
            }
            return adapted;
        }

        // Finds the best overload of `name` on `t` for `args` and invokes it.
        // Prefers an exact parameter-type match; falls back to the first overload
        // whose parameters ConvertArg can adapt the args to. Candidate signatures
        // are always logged - DXVision shapes are only visible through this log.
        private static object InvokeOn(string purpose, Type t, string name, bool wantStatic, object target, object[] args)
        {
            MethodInfo exact = null;
            MethodInfo fallback = null;
            MethodInfo[] methods;
            try { methods = t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch (Exception e) { throw new Day0GenException("cannot enumerate methods on " + t.FullName + ": " + e.Message, e); }
            foreach (MethodInfo m in methods)
            {
                if (m.Name != name || m.IsStatic != wantStatic) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != args.Length) continue;
                Log.Write("GENPROBE candidate " + DescribeMethod(m));
                if (fallback == null) fallback = m;
                if (exact == null)
                {
                    bool isExact = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (args[i] == null ? ps[i].ParameterType.IsValueType : ps[i].ParameterType != args[i].GetType())
                        { isExact = false; break; }
                    }
                    if (isExact) exact = m;
                }
            }
            MethodInfo chosen = exact != null ? exact : fallback;
            if (chosen == null)
                throw new Day0GenException("No overload of " + name + " with " + args.Length +
                                           " parameter(s) found on " + t.FullName);
            object[] adapted = AdaptArgs(purpose, chosen.GetParameters(), args);
            Log.Write("GENPROBE invoking " + purpose + " via " + DescribeMethod(chosen));
            return chosen.Invoke(target, adapted);
        }

        // Constructor twin of InvokeOn: tries every ctor whose arity matches,
        // adapting arguments; logs every candidate signature.
        private static object CreateWithAdaptedArgs(string purpose, Type t, object[] args)
        {
            Exception last = null;
            ConstructorInfo[] ctors;
            try { ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
            catch (Exception e) { throw new Day0GenException("cannot enumerate ctors on " + t.FullName + ": " + e.Message, e); }
            foreach (ConstructorInfo c in ctors)
            {
                Log.Write("GENPROBE ctor candidate " + DescribeMethod(c));
                ParameterInfo[] ps = c.GetParameters();
                if (ps.Length != args.Length) continue;
                object[] adapted;
                try { adapted = AdaptArgs(purpose, ps, args); }
                catch (Day0GenException e) { last = e; continue; }
                Log.Write("GENPROBE invoking " + purpose + " via " + DescribeMethod(c));
                return c.Invoke(adapted);
            }
            throw new Day0GenException("No usable constructor with " + args.Length +
                                       " parameter(s) on " + t.FullName, last);
        }

        // Weight callback for the reflected ChooseValueWithWeights<T> call: reads the
        // theme's PW property. Bound with Delegate.CreateDelegate's relaxed parameter
        // binding (Func<ZXMapTheme,float> accepts an (object)->float method).
        private static float GenProbeThemeWeight(object theme)
        {
            try
            {
                PropertyInfo p = FindPropertyUp(theme.GetType(), "PW");
                if (p != null)
                    return Convert.ToSingle(p.GetValue(theme, null), CultureInfo.InvariantCulture);
            }
            catch { }
            return 1f;
        }

        private static readonly MethodInfo genProbeWeightMethod = typeof(Program).GetMethod(
            "GenProbeThemeWeight", BindingFlags.Static | BindingFlags.NonPublic);

        private static void GenProbeStep1Theme()
        {
            // new DXRandom(seed)
            Type dxRandomType = FindTypeAnyOrder("DXVision.DXRandom", refl.DxAssembly);
            if (dxRandomType == null)
                throw new Day0GenException("Type DXVision.DXRandom not found in either assembly");
            object random = CreateWithAdaptedArgs("genprobe new DXRandom(seed)", dxRandomType,
                                                 new object[] { opts.Seed });
            Log.Write("GENPROBE step1: new DXRandom(" + opts.Seed + ") -> " + DescribeValue(random));

            // theme table: Dictionary<ZXMapThemeType, ZXMapTheme> (6 entries)
            object table = refl.Invoke("genprobe theme table", refl.ThemeTableMethod, null);
            System.Collections.IDictionary dict = table as System.Collections.IDictionary;
            if (dict == null)
                throw new Day0GenException("Theme table is not a dictionary: " + DescribeValue(table));
            PropertyInfo pwProp = FindPropertyUp(refl.MapThemeType, "PW");
            PropertyInfo mtProp = FindPropertyUp(refl.MapThemeType, "MapThemeType");
            object values = Activator.CreateInstance(typeof(List<>).MakeGenericType(refl.MapThemeType));
            System.Collections.IList valuesList = (System.Collections.IList)values;
            foreach (System.Collections.DictionaryEntry ent in dict)
            {
                object theme = ent.Value;
                valuesList.Add(theme);
                object pw = null;
                if (pwProp != null)
                {
                    try { pw = pwProp.GetValue(theme, null); } catch (Exception e) { pw = "<" + e.GetType().Name + ">"; }
                }
                Log.Write("GENPROBE step1: theme " + ent.Key + " -> " +
                          (theme == null ? "null" : theme.GetType().Name) + " PW=" + (pw == null ? "?" : pw.ToString()));
            }
            Log.Write("GENPROBE step1: theme table has " + dict.Count + " entr(ies); built List<" +
                      refl.MapThemeType.Name + "> with " + valuesList.Count + " theme object(s).");

            // ChooseValueWithWeights<T>: the generator calls it as a DXRandom extension
            // with a Dictionary<ZXMapTheme,float>; the believed shape is
            // (IEnumerable<T>, Func<T,float>). Discover ALL overloads, log their
            // signatures, then adapt to whichever fits.
            List<MethodInfo> chooseDefs = new List<MethodInfo>();
            ScanChooseValueWithWeights(refl.DxAssembly, chooseDefs);
            if (chooseDefs.Count == 0) ScanChooseValueWithWeights(refl.TabAssembly, chooseDefs);
            if (chooseDefs.Count == 0)
                throw new Day0GenException("No ChooseValueWithWeights<T> definition found in either assembly");

            object chosen = null;
            foreach (MethodInfo def in chooseDefs)
            {
                ParameterInfo[] ps = def.GetParameters();
                if (ps.Length != 2 || !ps[1].ParameterType.IsGenericType
                    || ps[1].ParameterType.GetGenericTypeDefinition() != typeof(Func<,>))
                    continue;
                Type[] wa = ps[1].ParameterType.GetGenericArguments();
                if (wa.Length != 2 || wa[1] != typeof(float) || !wa[0].IsGenericParameter)
                    continue;
                try
                {
                    MethodInfo closed = def.MakeGenericMethod(refl.MapThemeType);
                    ParameterInfo[] cps = closed.GetParameters();
                    if (!cps[0].ParameterType.IsInstanceOfType(values))
                    {
                        Log.Write("GENPROBE step1: NOTE: " + DescribeMethod(closed) +
                                  " param0 rejects List<ZXMapTheme>; trying next overload");
                        continue;
                    }
                    if (genProbeWeightMethod == null)
                        throw new Day0GenException("GenProbeThemeWeight method not resolvable");
                    object weightFn = Delegate.CreateDelegate(cps[1].ParameterType, genProbeWeightMethod);
                    chosen = closed.Invoke(null, new object[] { random, values, weightFn });
                    Log.Write("GENPROBE step1: invoked " + DescribeMethod(closed) + " (extension; random as arg0)");
                }
                catch (Exception e)
                {
                    Log.Write("GENPROBE step1: ChooseValueWithWeights overload failed: " + DescribeException(e));
                }
                if (chosen != null) break;
            }

            if (chosen == null)
            {
                // Fallback: 1-arg Dictionary<T,float> form (what the generator's own
                // call-site passes).
                foreach (MethodInfo def in chooseDefs)
                {
                    ParameterInfo[] ps = def.GetParameters();
                    if (ps.Length != 1 || !ps[0].ParameterType.IsGenericType) continue;
                    Type g = ps[0].ParameterType.GetGenericTypeDefinition();
                    if (g != typeof(Dictionary<,>) && g != typeof(System.Collections.Generic.IDictionary<,>)) continue;
                    Type[] ga = ps[0].ParameterType.GetGenericArguments();
                    if (ga.Length != 2 || ga[1] != typeof(float) || !ga[0].IsGenericParameter) continue;
                    try
                    {
                        MethodInfo closed = def.MakeGenericMethod(refl.MapThemeType);
                        object wdict = Activator.CreateInstance(
                            typeof(Dictionary<,>).MakeGenericType(refl.MapThemeType, typeof(float)));
                        System.Collections.IDictionary wdictIface = (System.Collections.IDictionary)wdict;
                        foreach (System.Collections.DictionaryEntry ent in dict)
                        {
                            if (pwProp == null) break;
                            wdictIface.Add(ent.Value, Convert.ToSingle(pwProp.GetValue(ent.Value, null),
                                                                     CultureInfo.InvariantCulture));
                        }
                        chosen = closed.Invoke(null, new object[] { random, wdict });
                        Log.Write("GENPROBE step1: invoked " + DescribeMethod(closed) + " (extension; random as arg0)");
                    }
                    catch (Exception e)
                    {
                        Log.Write("GENPROBE step1: ChooseValueWithWeights dictionary overload failed: " +
                                  DescribeException(e));
                    }
                    if (chosen != null) break;
                }
            }

            if (chosen == null)
                throw new Day0GenException("ChooseValueWithWeights: no usable overload among " +
                                           chooseDefs.Count + " candidate(s) logged above");
            string mt = "?";
            if (mtProp != null)
            {
                try { object mtv = mtProp.GetValue(chosen, null); mt = mtv == null ? "null" : mtv.ToString(); } catch { }
            }
            Log.Write("GENPROBE step1: chosen theme = " + DescribeValue(chosen) +
                      " (MapThemeType=" + mt + ")");
        }

        private static void ScanChooseValueWithWeights(Assembly asm, List<MethodInfo> found)
        {
            foreach (Type t in GameReflector.SafeGetTypes(asm))
            {
                if (t == null) continue;
                MethodInfo[] ms;
                try { ms = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                catch { continue; }
                foreach (MethodInfo m in ms)
                {
                    if (m.Name != "ChooseValueWithWeights" || !m.IsGenericMethodDefinition) continue;
                    found.Add(m);
                    Log.Write("GENPROBE step1: ChooseValueWithWeights definition on " +
                              (t.FullName ?? t.Name) + ": " + DescribeMethod(m));
                }
            }
        }

        // Step 1b: replicate the generator's theme pick EXACTLY (decompiled
        // `--zyl_...cs` lines ~84-95):
        //   DXRandom val = new DXRandom(params.Seed);
        //   List<ZXMapThemeType> { BR, AL, TM, DS, FA, VO };
        //   val.ChooseValueWithWeights<ZXMapTheme>(source.ToDictionary(
        //       k => table[k], k => table[k].PW));
        // Metadata shows ChooseValueWithWeights is an INSTANCE method on
        // DXVision.DXRandom (MemberRef DXRandom::ChooseValueWithWeights, generic,
        // 1 arg Dictionary<T,float>) - step 1's static-extension scan cannot see
        // it, so this step drives the instance method directly with a reflected
        // Dictionary<ZXMapTheme,float>.
        private static void GenProbeStep1bThemeExact()
        {
            Type dxRandomType = FindTypeAnyOrder("DXVision.DXRandom", refl.DxAssembly);
            if (dxRandomType == null)
                throw new Day0GenException("Type DXVision.DXRandom not found in either assembly");
            object random = CreateWithAdaptedArgs("genprobe1b new DXRandom(seed)", dxRandomType,
                                                 new object[] { opts.Seed });
            Log.Write("GENPROBE step1b: new DXRandom(" + opts.Seed + ") -> " + DescribeValue(random));

            object table = refl.Invoke("genprobe1b theme table", refl.ThemeTableMethod, null);
            System.Collections.IDictionary dict = table as System.Collections.IDictionary;
            if (dict == null)
                throw new Day0GenException("Theme table is not a dictionary: " + DescribeValue(table));
            PropertyInfo pwProp = FindPropertyUp(refl.MapThemeType, "PW");
            if (pwProp == null)
                throw new Day0GenException("ZXMapTheme.PW property not found");
            PropertyInfo mtProp = FindPropertyUp(refl.MapThemeType, "MapThemeType");

            // List<ZXMapThemeType> { BR, AL, TM, DS, FA, VO } - generator's key list;
            // its order fixes the Dictionary insertion order of the real call.
            string[] keyNames = new string[] { "BR", "AL", "TM", "DS", "FA", "VO" };
            object keys = Activator.CreateInstance(typeof(List<>).MakeGenericType(refl.MapThemeEnum));
            System.Collections.IList keyList = (System.Collections.IList)keys;
            foreach (string kn in keyNames)
                keyList.Add(Enum.Parse(refl.MapThemeEnum, kn));
            Log.Write("GENPROBE step1b: built List<" + refl.MapThemeEnum.Name + "> with " +
                      keyList.Count + " key(s) (BR,AL,TM,DS,FA,VO).");

            // source.ToDictionary(k => table[k], k => table[k].PW) via reflection:
            // Dictionary<ZXMapTheme,float>, key = the theme OBJECT, value = its PW.
            Type wdictType = typeof(Dictionary<,>).MakeGenericType(refl.MapThemeType, typeof(float));
            object wdict = Activator.CreateInstance(wdictType);
            System.Collections.IDictionary wdictIface = (System.Collections.IDictionary)wdict;
            foreach (object k in keyList)
            {
                object theme = dict[k];
                if (theme == null)
                    throw new Day0GenException("theme table[" + k + "] is null");
                float pw = Convert.ToSingle(pwProp.GetValue(theme, null), CultureInfo.InvariantCulture);
                wdictIface.Add(theme, pw);
                Log.Write("GENPROBE step1b: dict.Add(table[" + k + "]) key=" + DescribeValue(theme) +
                          " PW=" + pw.ToString("R", CultureInfo.InvariantCulture));
            }
            Log.Write("GENPROBE step1b: built " + wdictType.FullName + " with " + wdictIface.Count +
                      " entr(ies).");

            // Find ChooseValueWithWeights on the DXRandom type - INSTANCE method per
            // metadata, but scan instance+static to be safe. Pick the generic
            // definition whose single parameter is IDictionary/Dictionary/generic-
            // IEnumerable-compatible.
            MethodInfo chosenDef = null;
            MethodInfo[] ms;
            try { ms = dxRandomType.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                              BindingFlags.Public | BindingFlags.NonPublic); }
            catch (Exception e) { throw new Day0GenException("cannot enumerate methods on DXVision.DXRandom: " + e.Message, e); }
            foreach (MethodInfo m in ms)
            {
                if (m.Name != "ChooseValueWithWeights" || !m.IsGenericMethodDefinition) continue;
                Log.Write("GENPROBE step1b: ChooseValueWithWeights candidate " + DescribeMethod(m) +
                          (m.IsStatic ? " (static)" : " (instance)"));
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (!IsDictionaryLikeParameter(ps[0].ParameterType)) continue;
                if (chosenDef == null || (chosenDef.IsStatic && !m.IsStatic)) chosenDef = m;
            }
            if (chosenDef == null)
                throw new Day0GenException("No ChooseValueWithWeights<T> with a single dictionary-like " +
                                           "parameter found on " + dxRandomType.FullName);
            Log.Write("GENPROBE step1b: picked " + DescribeMethod(chosenDef) +
                      (chosenDef.IsStatic ? " (static)" : " (instance)"));

            MethodInfo closed = chosenDef.MakeGenericMethod(refl.MapThemeType);
            ParameterInfo[] cps = closed.GetParameters();
            if (!cps[0].ParameterType.IsInstanceOfType(wdict))
                throw new Day0GenException("Closed parameter " + cps[0].ParameterType.FullName +
                                           " rejects the reflected dictionary " + wdict.GetType().FullName);
            Log.Write("GENPROBE step1b: invoking " + DescribeMethod(closed) +
                      " on the DXRandom instance with the dictionary ...");
            try
            {
                object chosenTheme = closed.Invoke(random, new object[] { wdict });
                Log.Write("GENPROBE step1b: chosen theme = " + DescribeValue(chosenTheme));
                string mt = "?";
                if (mtProp != null)
                {
                    try
                    {
                        object mtv = mtProp.GetValue(chosenTheme, null);
                        mt = mtv == null ? "null" : mtv.ToString();
                    }
                    catch (Exception e) { mt = "<" + e.GetType().Name + ">"; }
                }
                Log.Write("GENPROBE step1b: theme MapThemeType=" + mt);
            }
            catch (Exception e)
            {
                LogExceptionChain("GENPROBE step1b invoke", e);
                throw;
            }
        }

        // True when `pt` can receive the generator's Dictionary<T,float> argument:
        // the closed generic itself, its open definition Dictionary<,>/IDictionary<,>/
        // IEnumerable<> and friends, or the non-generic IDictionary/IEnumerable.
        private static bool IsDictionaryLikeParameter(Type pt)
        {
            if (pt == null) return false;
            try { if (pt == typeof(System.Collections.IDictionary)) return true; } catch { }
            try { if (pt == typeof(System.Collections.IEnumerable)) return true; } catch { }
            if (!pt.IsGenericType) return false;
            try
            {
                Type g = pt.GetGenericTypeDefinition();
                return g == typeof(Dictionary<,>)
                    || g == typeof(System.Collections.Generic.IDictionary<,>)
                    || g == typeof(System.Collections.Generic.IReadOnlyDictionary<,>)
                    || g == typeof(IEnumerable<>)
                    || g == typeof(ICollection<>);
            }
            catch { return false; }
        }

        private static void GenProbeStep2WorldGridCreate()
        {
            Type gridType = FindTypeAnyOrder("DXVision.DXWorldGrid", refl.DxAssembly);
            if (gridType == null)
                throw new Day0GenException("Type DXVision.DXWorldGrid not found in either assembly");
            object result = InvokeOn("genprobe DXWorldGrid.Create", gridType, "Create", true, null,
                                     new object[] { opts.NCells, 0, 0, false });
            Log.Write("GENPROBE step2: DXWorldGrid.Create(" + opts.NCells + ",0,0,false) -> " +
                      DescribeValue(result));
        }

        private static void GenProbeStep3SceneSquareArea()
        {
            Type gridType = FindTypeAnyOrder("DXVision.DXWorldGrid", refl.DxAssembly);
            if (gridType == null)
                throw new Day0GenException("Type DXVision.DXWorldGrid not found in either assembly");
            object result = InvokeOn("genprobe DXWorldGrid.GetSceneSquareArea", gridType,
                                     "GetSceneSquareArea", true, null, new object[] { opts.NCells, 1f });
            Log.Write("GENPROBE step3: DXWorldGrid.GetSceneSquareArea(" + opts.NCells + ",1) -> " +
                      DescribeValue(result));
        }

        private static void GenProbeStep4ScenePointFromWorldCell()
        {
            Type gridType = FindTypeAnyOrder("DXVision.DXWorldGrid", refl.DxAssembly);
            if (gridType == null)
                throw new Day0GenException("Type DXVision.DXWorldGrid not found in either assembly");
            int half = opts.NCells / 2;
            object result = InvokeOn("genprobe DXWorldGrid.ScenePointFromWorldCell", gridType,
                                     "ScenePointFromWorldCell", true, null, new object[] { half, half });
            Log.Write("GENPROBE step4: DXWorldGrid.ScenePointFromWorldCell(" + half + "," + half + ") -> " +
                      DescribeValue(result));
        }

        private static void GenProbeStep5NoyseLayer()
        {
            Type layerType = FindTypeAnyOrder("DXVision.DXNoyseLayer", refl.DxAssembly);
            if (layerType == null)
                throw new Day0GenException("Type DXVision.DXNoyseLayer not found in either assembly");
            object layer = CreateWithAdaptedArgs("genprobe new DXNoyseLayer(n)", layerType,
                                                new object[] { opts.NCells });
            Log.Write("GENPROBE step5: new DXNoyseLayer(" + opts.NCells + ") -> " + DescribeValue(layer));

            object cloneHolder = null;
            List<string> failures = new List<string>();

            if (!ProbeOp("step5 FillWithNoyse(100,0.03,0.03,200,300,2)", delegate
            {
                InvokeOn("genprobe FillWithNoyse", layerType, "FillWithNoyse", false, layer,
                         new object[] { 100.0, 0.03, 0.03, 200.0, 300.0, 2 });
            })) failures.Add("FillWithNoyse");

            if (!ProbeOp("step5 Clone()", delegate
            {
                cloneHolder = InvokeOn("genprobe Clone", layerType, "Clone", false, layer, new object[0]);
            })) failures.Add("Clone");

            if (!ProbeOp("step5 Substract(clone)", delegate
            {
                if (cloneHolder == null) throw new Day0GenException("no clone from previous op");
                InvokeOn("genprobe Substract", layerType, "Substract", false, layer,
                         new object[] { cloneHolder });
            })) failures.Add("Substract");

            if (!ProbeOp("step5 MultiplyWith(clone)", delegate
            {
                if (cloneHolder == null) throw new Day0GenException("no clone from previous op");
                InvokeOn("genprobe MultiplyWith", layerType, "MultiplyWith", false, layer,
                         new object[] { cloneHolder });
            })) failures.Add("MultiplyWith");

            if (!ProbeOp("step5 SetContrast(2.0)", delegate
            {
                InvokeOn("genprobe SetContrast", layerType, "SetContrast", false, layer, new object[] { 2.0 });
            })) failures.Add("SetContrast");

            if (!ProbeOp("step5 TruncateTo01(0.5,1)", delegate
            {
                InvokeOn("genprobe TruncateTo01", layerType, "TruncateTo01", false, layer,
                         new object[] { 0.5, 1 });
            })) failures.Add("TruncateTo01");

            if (!ProbeOp("step5 GetAreaNearPoint(half,half,8,8,1.0)", delegate
            {
                object r = InvokeOn("genprobe GetAreaNearPoint", layerType, "GetAreaNearPoint", false,
                                    layer, new object[] { opts.NCells / 2, opts.NCells / 2, 8, 8, 1.0 });
                Log.Write("GENPROBE step5: GetAreaNearPoint -> " + DescribeValue(r));
            })) failures.Add("GetAreaNearPoint");

            if (!ProbeOp("step5 GetTotalValueOnArea(FromCenter rect)", delegate
            {
                object rect = GenProbeBuildFromCenterRect();
                if (rect == null)
                {
                    Log.Write("GENPROBE step5: SKIP GetTotalValueOnArea - no usable FromCenter(point,w,h) found");
                    return;
                }
                object total = InvokeOn("genprobe GetTotalValueOnArea", layerType, "GetTotalValueOnArea",
                                        false, layer, new object[] { rect });
                Log.Write("GENPROBE step5: GetTotalValueOnArea -> " + DescribeValue(total));
            })) failures.Add("GetTotalValueOnArea");

            if (failures.Count > 0)
                throw new Day0GenException("DXNoyseLayer sub-ops failed: " +
                                           string.Join(", ", failures.ToArray()));
        }

        // Builds the Rectangle argument for GetTotalValueOnArea via
        // DXExtensions_Rectangle / DXHelper_Rectangle.FromCenter (whichever exists
        // with an invocable shape). Returns null when neither is usable; the caller
        // then logs SKIP instead of failing the step.
        private static object GenProbeBuildFromCenterRect()
        {
            object center = new System.Drawing.Point(opts.NCells / 2, opts.NCells / 2);
            Type[] helperTypes = new Type[]
            {
                FindTypeAnyOrder("DXVision.DXExtensions_Rectangle", refl.DxAssembly),
                FindTypeAnyOrder("DXVision.DXHelper_Rectangle", refl.DxAssembly)
            };
            foreach (Type ht in helperTypes)
            {
                if (ht == null) continue;
                MethodInfo[] ms;
                try { ms = ht.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                catch { continue; }
                foreach (MethodInfo m in ms)
                {
                    if (m.Name != "FromCenter") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    Log.Write("GENPROBE candidate " + DescribeMethod(m));
                    if (ps.Length != 2 && ps.Length != 3) continue;
                    try
                    {
                        object[] args = ps.Length == 3
                            ? new object[] { center, 8, 8 }
                            : new object[] { center, 8 };
                        object rect = m.Invoke(null, AdaptArgs("genprobe FromCenter", ps, args));
                        Log.Write("GENPROBE step5: FromCenter -> " + DescribeValue(rect));
                        return rect;
                    }
                    catch (Exception e)
                    {
                        Log.Write("GENPROBE step5: FromCenter candidate failed: " + DescribeException(e));
                    }
                }
            }
            return null;
        }

        private static void GenProbeStep6MapDrawer()
        {
            Type drawerType = FindTypeAnyOrder("ZX.GameSystems.ZXMapDrawer", refl.TabAssembly);
            if (drawerType == null)
                throw new Day0GenException("Type ZX.GameSystems.ZXMapDrawer not found in either assembly");
            object drawer = CreateWithAdaptedArgs("genprobe new ZXMapDrawer(n)", drawerType,
                                                 new object[] { opts.NCells });
            Log.Write("GENPROBE step6: new ZXMapDrawer(" + opts.NCells + ") -> " + DescribeValue(drawer));
            string[] propNames = new string[] { "LayerTerrain", "LayerObjects", "ExtraEntities" };
            foreach (string pn in propNames)
            {
                PropertyInfo p = FindPropertyUp(drawerType, pn);
                if (p == null)
                {
                    Log.Write("GENPROBE step6: ZXMapDrawer." + pn + ": property not found");
                    continue;
                }
                try
                {
                    object val = p.GetValue(drawer, null);
                    Log.Write("GENPROBE step6: ZXMapDrawer." + pn + " = " + DescribeValue(val));
                }
                catch (Exception e)
                {
                    Log.Write("GENPROBE step6: ZXMapDrawer." + pn + " read FAILED: " + DescribeException(e));
                }
            }
        }

        private static void GenProbeStep7TemplateChain()
        {
            if (refl.DxProjectFromIdMethod == null)
                throw new Day0GenException("DXProject.FromID was not discovered");
            object project = refl.DxProjectFromIdMethod.Invoke(null, new object[] { ProjectId });
            if (project == null)
                throw new Day0GenException("DXProject.FromID(" + ProjectId + ") returned null");
            Log.Write("GENPROBE step7: DXProject.FromID(" + ProjectId + ") -> " + DescribeValue(project));

            PropertyInfo etProp = FindPropertyUp(project.GetType(), "EntityTemplates");
            object templates;
            if (etProp != null)
            {
                templates = etProp.GetValue(project, null);
            }
            else
            {
                FieldInfo etField = FindFieldUp(project.GetType(), "EntityTemplates");
                if (etField == null)
                    throw new Day0GenException("DXProject.EntityTemplates member not found");
                templates = etField.GetValue(project);
            }
            System.Collections.IDictionary td = templates as System.Collections.IDictionary;
            if (td == null)
                throw new Day0GenException("EntityTemplates is not IDictionary: " + DescribeValue(templates));
            Log.Write("GENPROBE step7: EntityTemplates type=" + templates.GetType().FullName +
                      " Count=" + td.Count);

            bool has = td.Contains(GenProbeCommandCenterTemplateId);
            Log.Write("GENPROBE step7: ContainsKey(" + GenProbeCommandCenterTemplateId + ") = " + has);
            object template = td[GenProbeCommandCenterTemplateId];
            if (template == null)
                throw new Day0GenException("EntityTemplates[" + GenProbeCommandCenterTemplateId + "] is null");
            Log.Write("GENPROBE step7: template = " + DescribeValue(template));

            MethodInfo ci = null;
            foreach (MethodInfo m in template.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "CreateInstance") continue;
                Log.Write("GENPROBE step7 candidate " + DescribeMethod(m));
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.Name == "DXRandom") ci = m;
            }
            if (ci == null)
                throw new Day0GenException("DXEntityTemplate.CreateInstance(DXRandom) not found");
            object entity = ci.Invoke(template, new object[] { null });
            Log.Write("GENPROBE step7: CreateInstance(null) -> " + DescribeValue(entity));

            PropertyInfo cellProp = FindPropertyUp(entity.GetType(), "Cell");
            if (cellProp == null)
                throw new Day0GenException("DXEntity.Cell property not found");
            object cell = new System.Drawing.Point(opts.NCells / 2, opts.NCells / 2);
            cellProp.SetValue(entity, ConvertArg(cell, cellProp.PropertyType), null);
            Log.Write("GENPROBE step7: entity.Cell = " + DescribeValue(cellProp.GetValue(entity, null)));

            PropertyInfo nameProp = FindPropertyUp(entity.GetType(), "Name");
            string nm = "(no Name property)";
            if (nameProp != null)
            {
                try
                {
                    object nv = nameProp.GetValue(entity, null);
                    nm = nv == null ? "null" : nv.ToString();
                }
                catch (Exception e) { nm = "<" + e.GetType().Name + ">"; }
            }
            Log.Write("GENPROBE step7: entity type=" + entity.GetType().FullName + " Name=" + nm);
        }

        private static void GenProbeStep8Generator()
        {
            int[] waits = new int[] { 0, 5, 10 };   // seconds to sleep BEFORE attempts 2 and 3
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (waits[attempt - 1] > 0)
                {
                    Log.Write("GENPROBE step8: sleeping " + waits[attempt - 1] +
                              "s before attempt " + attempt + " ...");
                    Thread.Sleep(waits[attempt - 1] * 1000);
                }
                object p = refl.CreateInstance("genprobe new ZXRandomLevelParams() (attempt " + attempt + ")",
                                               refl.ParamsType);
                refl.SetProp("genprobe params.Seed", refl.ParamsType.GetProperty("Seed"), p, opts.Seed);
                refl.SetProp("genprobe params.NCells", refl.ParamsType.GetProperty("NCells"), p, opts.NCells);
                refl.SetProp("genprobe params.ThemeType=None", refl.ParamsType.GetProperty("ThemeType"), p,
                             Enum.Parse(refl.MapThemeEnum, "None"));
                refl.SetProp("genprobe params.FactorGameDuration",
                             refl.ParamsType.GetProperty("FactorGameDuration"), p, opts.Duration);
                refl.SetProp("genprobe params.FactorZombiePopulation",
                             refl.ParamsType.GetProperty("FactorZombiePopulation"), p, opts.Pop);
                refl.SetProp("genprobe params.Name", refl.ParamsType.GetProperty("Name"), p, "probe");
                Log.Write("GENPROBE step8: attempt " + attempt + " - invoking generator (Seed=" + opts.Seed +
                          ", NCells=" + opts.NCells + ", ThemeType=None, Duration=" + opts.Duration +
                          ", Pop=" + opts.Pop + ", Name=probe) ...");
                try
                {
                    object level = refl.Invoke("genprobe generator(params) attempt " + attempt,
                                              refl.GenerateMethod, null, p);
                    if (level != null)
                    {
                        Log.Write("GENPROBE step8: SUCCESS on attempt " + attempt +
                                  " - generator returned " + level.GetType().FullName);
                        return;
                    }
                    Log.Write("GENPROBE step8: attempt " + attempt + " returned a NULL level.");
                }
                catch (Day0GenException e)
                {
                    Log.Write("GENPROBE step8: attempt " + attempt + " threw: " + e.Message);
                    LogExceptionChain("GENPROBE step8 attempt " + attempt, e);
                }
            }
            Log.Write("GENPROBE step8: all 3 generator attempts failed or returned null.");
        }

        // ---------------------------------------------------------------------
        // Engine UI-thread marshal target. The engine (DXVision) is WinForms: it
        // pumps messages on its own STA thread, and engine-side sequences that
        // mutate scene state must run there (in the real game they live in click
        // handlers on the message-loop thread). Preference: the Application.OpenForms
        // form whose handle equals the process MainWindowHandle; fallback
        // Control.FromHandle(MainWindowHandle). Returns null when neither yields a
        // Control - the caller then runs inline on its own thread (previous
        // behavior). Fills the uiMarshal* diagnostics fields for the pre-Invoke log.
        // ---------------------------------------------------------------------
        private static Control FindEngineUiMarshalTarget()
        {
            uiMarshalUsedFallback = false;
            uiMarshalMainWindowHandle = IntPtr.Zero;
            uiMarshalFormCount = 0;

            // MainWindowHandle is a cached snapshot; Refresh() re-reads it. The engine
            // sits at its main menu by now, so the handle should appear quickly; poll
            // up to ~30s to tolerate slower frames.
            DateTime handleDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < handleDeadline)
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    self.Refresh();
                    if (self.MainWindowHandle != IntPtr.Zero)
                    {
                        uiMarshalMainWindowHandle = self.MainWindowHandle;
                        break;
                    }
                }
                Thread.Sleep(500);
            }
            if (uiMarshalMainWindowHandle == IntPtr.Zero)
                Log.Write("UI-marshal: MainWindowHandle still zero after ~30s of polling.");

            // Snapshot OpenForms once: live enumeration races form create/close on
            // the engine thread.
            List<Form> formList = new List<Form>();
            try
            {
                foreach (Form f in Application.OpenForms) formList.Add(f);
            }
            catch (Exception e)
            {
                Log.Write("UI-marshal: OpenForms enumeration threw: " + e.GetType().Name + ": " + e.Message);
            }
            uiMarshalFormCount = formList.Count;
            Log.Write("UI-marshal: OpenForms count=" + uiMarshalFormCount + ".");
            for (int i = 0; i < formList.Count; i++)
            {
                Log.Write("UI-marshal: form[" + i + "] Name='" + SafeControlName(formList[i]) +
                          "' Text='" + SafeControlText(formList[i]) + "' Handle=" +
                          SafeControlHandle(formList[i]) + ".");
            }

            if (uiMarshalMainWindowHandle != IntPtr.Zero)
            {
                for (int i = 0; i < formList.Count; i++)
                {
                    try
                    {
                        if (formList[i].Handle == uiMarshalMainWindowHandle) return formList[i];
                    }
                    catch (Exception)
                    {
                        // handle not created - skip this form
                    }
                }

                Control fromHandle = null;
                try { fromHandle = Control.FromHandle(uiMarshalMainWindowHandle); }
                catch (Exception e)
                {
                    Log.Write("UI-marshal: Control.FromHandle threw: " + e.GetType().Name + ": " + e.Message);
                }
                if (fromHandle != null)
                {
                    uiMarshalUsedFallback = true;
                    return fromHandle;
                }
            }

            return null;
        }

        // Cross-thread control reads for logging only; WinForms property getters are
        // not guaranteed thread-safe, so degrade to a placeholder instead of throwing.
        private static string SafeControlName(Control c)
        {
            try { return c.Name == null ? "" : c.Name; }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        private static string SafeControlText(Control c)
        {
            try { return c.Text == null ? "" : c.Text; }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        private static string SafeControlHandle(Control c)
        {
            try { return c.Handle.ToString(); }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        // ---------------------------------------------------------------------
        // Runs a whole sequence on the engine UI thread via Control.Invoke on the
        // marshal target (FindEngineUiMarshalTarget), with a 15-min watchdog that
        // aborts the process when the engine loop stops pumping messages
        // (deadlock protection: Control.Invoke blocks until the pump runs the
        // delegate). Falls back to the current thread (previous, racy behavior)
        // when no marshal target exists. stallNote is logged by the watchdog for
        // operator follow-up. Shared by phase full and phase genprobe.
        // ---------------------------------------------------------------------
        private static void RunOnEngineUiThread(string sequenceName, string stallNote, MethodInvoker body)
        {
            Control uiMarshal = FindEngineUiMarshalTarget();
            if (uiMarshal == null)
            {
                Log.Write("WARNING: no engine UI-thread marshal target found (MainWindowHandle=" +
                          uiMarshalMainWindowHandle + ", OpenForms count=" + uiMarshalFormCount +
                          "; neither OpenForms handle match nor Control.FromHandle yielded a Control). " +
                          "Running " + sequenceName + " on the CURRENT thread (id=" +
                          Thread.CurrentThread.ManagedThreadId + ") - previous, racy behavior.");
                body();
                return;
            }

            Log.Write("UI-marshal: invoking " + sequenceName + " on engine UI thread. " +
                      "MainWindowHandle=" + uiMarshalMainWindowHandle + ", OpenForms count=" +
                      uiMarshalFormCount + " (names/titles above), caller thread id=" +
                      Thread.CurrentThread.ManagedThreadId + ", fallback used=" +
                      (uiMarshalUsedFallback ? "yes (Control.FromHandle)" : "no (OpenForms handle match)") +
                      ", marshal control=" + uiMarshal.GetType().Name + " '" +
                      SafeControlName(uiMarshal) + "'.");

            Exception marshalError = null;
            bool marshalFinished = false;
            Thread uiWatchdog = new Thread(delegate()
            {
                DateTime armedAt = DateTime.UtcNow;
                while (!marshalFinished)
                {
                    Thread.Sleep(1000);
                    if (marshalFinished) return;
                    if (DateTime.UtcNow - armedAt >= TimeSpan.FromMinutes(15))
                    {
                        Log.Write("UI-thread marshal watchdog fired - engine loop appears not to pump " +
                                  "messages; aborting process; " + stallNote);
                        Environment.Exit(2);
                    }
                }
            });
            uiWatchdog.IsBackground = true;
            uiWatchdog.Start();
            try
            {
                uiMarshal.Invoke((MethodInvoker)delegate
                {
                    try { body(); }
                    catch (Exception ex) { marshalError = ex; }
                });
            }
            finally
            {
                marshalFinished = true;
            }
            if (marshalError != null)
            {
                Log.Write("UI-thread marshaled " + sequenceName + " FAILED; exception chain:");
                LogExceptionChain("MARSHAL", marshalError);
                if (marshalError is Day0GenException) throw marshalError;
                throw new Day0GenException("UI-thread marshaled sequence failed unexpectedly.", marshalError);
            }
        }

        // ---------------------------------------------------------------------
        // Phase: full — construction + generation + save + verify
        // ---------------------------------------------------------------------
        private static int RunFull()
        {
            RefuseIfGameRunning();
            VerifyTabDir();

            string savesDir = Path.GetFullPath(opts.SavesDirExplicit ? opts.SavesDir : opts.DefaultSavesDir());
            if (!Directory.Exists(savesDir))
                throw new Day0GenException("Saves dir does not exist: " + savesDir);
            string rootDir = RootDirOf(savesDir);
            string target = Path.Combine(savesDir, opts.Name + ".zxsav");
            string checkPath = Path.Combine(savesDir, opts.Name + ".zxcheck");
            if (File.Exists(target) || File.Exists(checkPath))
                throw new Day0GenException("Refusing to overwrite: " + target + " / " + checkPath + " already exists.");

            Log.Write("Snapshot BEFORE (saves dir + parent): " + savesDir + " ; " + rootDir);
            DirSnapshot before = DirSnapshot.Take(savesDir);
            before.Add(rootDir);

            refl = new GameReflector();
            refl.LoadAssemblies();
            refl.DiscoverAll(false);
            ZombieInit();               // includes account + theme table gates
            ProbePasswordMachinery();

            // resolve effective saves dir again now that the engine can tell us
            string effectiveSavesDir = Path.GetFullPath(EffectiveSavesDir());
            if (string.Compare(effectiveSavesDir, savesDir, StringComparison.OrdinalIgnoreCase) != 0)
            {
                Log.Write("WARNING: engine saves dir (" + effectiveSavesDir + ") differs from initial (" + savesDir +
                          "); writing to the ENGINE's dir and hashing both trees.");
                target = Path.Combine(effectiveSavesDir, opts.Name + ".zxsav");
                checkPath = Path.Combine(effectiveSavesDir, opts.Name + ".zxcheck");
                if (File.Exists(target) || File.Exists(checkPath))
                    throw new Day0GenException("Refusing to overwrite (engine dir): " + target + " already exists.");
                before.Add(effectiveSavesDir);
                before.Add(RootDirOf(effectiveSavesDir));
            }

            if (opts.ValidateSigner != null) ValidateSigner(opts.ValidateSigner);

            WaitForProjectContext();

            // ---- marshal the construct/generate/save sequence onto the engine UI thread.
            // The zombie engine fully initializes to its main menu - a live WinForms
            // message pump on the engine thread. In the real game this whole sequence
            // runs inside a click handler on that thread; run from this thread it races
            // the engine render loop (ZXGameState.Set / CurrentGameSystem assignment
            // interleave with the engine's own scene changes) and the generator dies
            // with a NullReferenceException on thread-affine state.
            RunOnEngineUiThread("construct/generate/save",
                "if partial files exist, delete '" + target + "' / '" + checkPath + "' manually after review.",
                delegate { RunConstructGenerateSave(target, checkPath, effectiveSavesDir); });

            // ---- after snapshot -------------------------------------------------------
            Log.Write("Snapshot AFTER ...");
            DirSnapshot after = DirSnapshot.Take(effectiveSavesDir);
            after.Add(RootDirOf(effectiveSavesDir));
            if (string.Compare(effectiveSavesDir, savesDir, StringComparison.OrdinalIgnoreCase) != 0)
            {
                after.Add(savesDir);
                after.Add(rootDir);
            }
            List<string> changes = DirSnapshot.Diff(before, after);
            List<string> unexpected = new List<string>();
            foreach (string c in changes)
            {
                Log.Write("CHANGE: " + c);
                // change lines use the "<VERB>: <path>" form; ':' cannot occur in
                // Windows file names so the first ": " cleanly separates
                string path = c.Substring(c.IndexOf(": ") + 2).Trim();
                string fileName = Path.GetFileName(path);
                bool allowed = string.Compare(fileName, opts.Name + ".zxsav", StringComparison.OrdinalIgnoreCase) == 0
                            || string.Compare(fileName, opts.Name + ".zxcheck", StringComparison.OrdinalIgnoreCase) == 0
                            || string.Compare(fileName, "ZXLog.txt", StringComparison.OrdinalIgnoreCase) == 0;
                if (!allowed) unexpected.Add(c);
            }
            if (unexpected.Count > 0)
            {
                foreach (string u in unexpected) Log.Write("UNEXPECTED FILE CHANGE: " + u);
                throw new Day0GenException("Unexpected file changes detected (see log). Only " + opts.Name +
                                           ".zxsav/.zxcheck (+engine ZXLog.txt) may change.");
            }
            if (changes.Count == 0) Log.Write("No file changes detected (unexpected for a full run - investigate).");

            Log.Write("PHASE full COMPLETE: " + target);
            Log.Write("=== Day0Gen OK ===");
            return 0;
        }

        // ---------------------------------------------------------------------
        // Construction + generation + save + verification, extracted from RunFull so
        // the whole sequence can be marshaled onto the engine UI thread via
        // Control.Invoke (or, when no marshal target was found, run inline on the
        // current thread - the previous behavior). Reads the static opts / refl /
        // managerInstance fields; the before/after snapshot context lives in RunFull
        // and is not needed here.
        // ---------------------------------------------------------------------
        private static void RunConstructGenerateSave(string target, string checkPath, string effectiveSavesDir)
        {
            // ---- construction: mirror CC handler minus challenge lines ------------
            Log.Write("Constructing game state (name='" + opts.Name + "') ...");
            object gs = refl.Invoke("new ZXGameState(name)", refl.GameStateCtorName, null, opts.Name);
            refl.Invoke("ZXGameState.Set(gs)", refl.GameStateSetMethod, null, gs);
            refl.SetProp("gs.GameMode=Survival", refl.GameStateType.GetProperty("GameMode"), gs,
                         Enum.Parse(refl.GameModeEnum, "Survival"));

            object p = refl.CreateInstance("new ZXRandomLevelParams()", refl.ParamsType);
            Log.Write("Created ZXRandomLevelParams (defaults untouched except below).");
            refl.SetProp("params.Seed", refl.ParamsType.GetProperty("Seed"), p, opts.Seed);
            refl.SetProp("params.NCells", refl.ParamsType.GetProperty("NCells"), p, opts.NCells);
            refl.SetProp("params.FactorGameDuration", refl.ParamsType.GetProperty("FactorGameDuration"), p, opts.Duration);
            refl.SetProp("params.FactorZombiePopulation", refl.ParamsType.GetProperty("FactorZombiePopulation"), p, opts.Pop);
            refl.SetProp("params.Name", refl.ParamsType.GetProperty("Name"), p, opts.Name);
            refl.SetProp("params.ThemeType=None", refl.ParamsType.GetProperty("ThemeType"), p,
                         Enum.Parse(refl.MapThemeEnum, "None"));
            // DifficultyType: intentionally left at default (None) - CC handler parity.
            // ChallengeType: intentionally left Default - never CommunityChallenge.

            refl.SetProp("gs.SurvivalModeParams", refl.GameStateType.GetProperty("SurvivalModeParams"), gs, p);

            object ls = refl.CreateInstance("new ZXLevelState()", refl.LevelStateType);
            refl.Invoke("ZXLevelState.Set(ls)", refl.LevelStateSetMethod, null, ls);
            refl.Invoke("ZXLevelState.Init()", refl.LevelStateInitMethod, ls);

            object sys = refl.Invoke("DXSystem.Load<gamesystem>(false)",
                refl.DxSystemLoadMethod.MakeGenericMethod(refl.GameSystemType), null, false);
            refl.SetProp("manager.CurrentGameSystem=sys", refl.CurrentGameSystemProp, managerInstance, sys);

            Log.Write("Effective ZXRandomLevelParams read back before generation:");
            refl.GetProp("params.Seed", refl.ParamsType.GetProperty("Seed"), p);
            refl.GetProp("params.NCells", refl.ParamsType.GetProperty("NCells"), p);
            refl.GetProp("params.ThemeType", refl.ParamsType.GetProperty("ThemeType"), p);
            refl.GetProp("params.FactorGameDuration", refl.ParamsType.GetProperty("FactorGameDuration"), p);
            refl.GetProp("params.FactorZombiePopulation", refl.ParamsType.GetProperty("FactorZombiePopulation"), p);
            refl.GetProp("params.Name", refl.ParamsType.GetProperty("Name"), p);

            LogProjectDiagnostics();

            // Bracket the engine-side ZXLog output around the generation call so the
            // interleaving with the engine's own scene-change logs is visible.
            DumpZxLogTail(effectiveSavesDir, 15);
            Log.Write("Generating level (engine logs 'Random Map Creation with seed: " + opts.Seed + "') ...");
            object level = refl.Invoke("generator(params)", refl.GenerateMethod, null, p);
            if (level == null)
                throw new Day0GenException("Generator returned null level.");
            refl.Invoke("gamesystem.SetLevel(level)", refl.SetLevelMethod, sys, level);

            DumpZxLogTail(effectiveSavesDir, 15);

            // ---- save: use the game's own writer (password + Info/Data + zxcheck) --
            Log.Write("Writing save: " + target);
            if (refl.SaveWriterMethod != null)
            {
                refl.Invoke("manager save writer (native)", refl.SaveWriterMethod, managerInstance, target);
            }
            else
            {
                Log.Write("Falling back to manual save composition (ZipSerializer.Write).");
                ManualSave(target, gs);
            }
            if (!File.Exists(target)) throw new Day0GenException("Save writer did not produce " + target);
            if (!File.Exists(checkPath)) throw new Day0GenException("Save writer did not produce " + checkPath);

            // ---- verify ------------------------------------------------------------
            string sig = (string)refl.Invoke("signing(target,2)", refl.SigningMethod, null, target, 2);
            string written = File.ReadAllText(checkPath).Trim();
            if (sig != written)
                throw new Day0GenException("zxcheck mismatch: file contains '" + written + "', signer produced '" + sig + "'.");
            Log.Write("zxcheck verified: " + sig);

            object readBack = ReadBackState(target);
            if (readBack == null)
                throw new Day0GenException("Read-back of saved state returned null.");
            object readName = refl.GetProp("read-back.Name", refl.GameStateType.GetProperty("Name"), readBack);
            if (string.Compare((string)readName, opts.Name, StringComparison.Ordinal) != 0)
                throw new Day0GenException("Read-back name '" + readName + "' != '" + opts.Name + "'.");
            Log.Write("Read-back OK: ZXGameState named '" + readName + "'.");

            object list = refl.Invoke("manager save list", refl.SaveListMethod, managerInstance);
            bool listed = false;
            System.Collections.IEnumerable en = list as System.Collections.IEnumerable;
            if (en != null)
            {
                PropertyInfo nameProp = refl.GameStateInfoType.GetProperty("Name");
                foreach (object info in en)
                {
                    object n = nameProp.GetValue(info, null);
                    if (n != null && string.Compare((string)n, opts.Name, StringComparison.Ordinal) == 0)
                    {
                        listed = true;
                        break;
                    }
                }
            }
            if (!listed)
                throw new Day0GenException("Manager save list does not contain an entry named '" + opts.Name + "'.");
            Log.Write("Save-list verification OK.");
        }

        private static void ManualSave(string target, object gs)
        {
            // Mirrors manager _0023_003DzMtGuEM2lBSlZ5BGWvg== + zxcheck writer exactly:
            // set password(path,2,false); Write(path,"Data",gs,"Info",info); clear; sign.
            refl.Invoke("password generator(set,write)", refl.PwdSetMethod, null, target, 2, false);
            object info = refl.Invoke("gs.BuildInfo(filename)", refl.StateInfoMethod, gs, Path.GetFileName(target));
            refl.Invoke("ZipSerializer.Write(Data,Info)", refl.ZipWriteMethod, null,
                        target, "Data", gs, "Info", info);
            refl.Invoke("password generator(clear,write)", refl.PwdClearMethod, null, target, 2, false);
            string sig = (string)refl.Invoke("signing(target,2)", refl.SigningMethod, null, target, 2);
            File.WriteAllText(Path.ChangeExtension(target, ".zxcheck"), sig);
            Log.Write("Manual save composition done (zxcheck: " + sig + ").");
        }

        private static object ReadBackState(string target)
        {
            // Replicates the game's load path: flag(path) -> set password -> read
            // "Data" entry -> clear. Read-only.
            object flagObj = refl.Invoke("password flag(target)", refl.FlagMethod, null, target);
            int flag = (int)flagObj;
            refl.Invoke("password generator(set,read)", refl.PwdSetMethod, null, target, flag, true);
            object state;
            if (refl.ZxFileReadMethod != null)
                state = refl.Invoke("ZXFile<ZXGameState>.read(target)", refl.ZxFileReadMethod, null, target);
            else
                state = refl.Invoke("ZipSerializer.Read(target,'Data')", refl.ZipReadMethod, null, target, "Data");
            if (refl.PwdClearMethod != null)
            {
                try { refl.Invoke("password generator(clear,read)", refl.PwdClearMethod, null, target, flag, true); }
                catch (Day0GenException e) { Log.Write("password clear after read failed (non-fatal): " + e.Message); }
            }
            return state;
        }
    }
}
