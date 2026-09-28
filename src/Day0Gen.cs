// Day0Gen — day-0 survival save generator for They Are Billions (v1.0.14).
// Mirrors the game's own survival/community-challenge start handler via runtime
// reflection only (zero compile-time game references). See SPEC.md / PLAN.md.
//
// C# 5 compatible, target net48. Must be run from the TAB install directory
// (operator places the exe there) so Assembly.Load("TheyAreBillions") resolves.
//
// Phases: discovery | zombie | full (later phases imply earlier ones).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

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
        public string Phase = null;                     // discovery | zombie | full
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
                throw new Day0GenException("--phase discovery|zombie|full is required");
            o.Phase = o.Phase.ToLowerInvariant();
            if (o.Phase != "discovery" && o.Phase != "zombie" && o.Phase != "full")
                throw new Day0GenException("Invalid --phase '" + o.Phase + "' (discovery|zombie|full)");
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

        private static Type[] SafeGetTypes(Assembly asm)
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
                throw new Day0GenException("Reflection invocation failed: " + purpose, root);
            }
            Log.Write("INVOKE OK " + purpose + " -> " + ShortResult(result));
            return result;
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
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_MINIMIZE = 6;

        private static Options opts;
        private static GameReflector refl;
        private static volatile bool engineThreadDead;

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
            return "Day0Gen --phase discovery|zombie|full [--tab-dir <dir>] [--saves-dir <dir>] [--seed N]\r\n"
                 + "        [--ncells N] [--duration F] [--pop F] [--name S] [--validate-signer <zxsav>]";
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

            // Poll: minimize popup whenever it appears; wait for manager singleton.
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            bool popupMinimized = false;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (Process self = Process.GetCurrentProcess())
                    {
                        IntPtr hwnd = self.MainWindowHandle;
                        if (hwnd != IntPtr.Zero && !popupMinimized)
                        {
                            ShowWindow(hwnd, SW_MINIMIZE);
                            popupMinimized = true;
                            Log.Write("Engine popup detected and minimized.");
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Write("popup poll: " + e.Message);
                }

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
        // Wait for the engine's scene/project context. ZXLevelState's ctor reads
        // DXProject.Current (CurrentGeneratedLevel ?? DXProject.Current.LevelFromID(...)),
        // which is only populated once the zombie engine finishes scene/project init.
        // ---------------------------------------------------------------------
        private static void WaitForProjectContext()
        {
            if (refl.DxProjectType == null ||
                (refl.DxProjectCurrentProp == null && refl.DxProjectCurrentField == null))
                throw new Day0GenException("DXProject.Current was not discovered; cannot verify engine " +
                                           "scene/project init before construction.");

            Log.Write("Waiting for DXProject.Current (engine scene/project context) ...");
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            int polls = 0;
            while (DateTime.UtcNow < deadline && !engineThreadDead)
            {
                object current = null;
                try
                {
                    // Prefer the property if present, otherwise read the field.
                    if (refl.DxProjectCurrentProp != null)
                        current = refl.DxProjectCurrentProp.GetValue(null, null);
                    else
                        current = refl.DxProjectCurrentField.GetValue(null);
                }
                catch (Exception e)
                {
                    Exception root = e;
                    if (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
                    if (polls == 0)
                        Log.Write("DXProject.Current getter threw (will keep polling): " +
                                  root.GetType().Name + ": " + root.Message);
                }
                polls++;
                if (current != null)
                {
                    Log.Write("DXProject.Current ready after " + polls + " poll(s): " + current.GetType().FullName);
                    return;
                }
                if (polls % 20 == 0)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalSeconds;
                    if (left < 0) left = 0;
                    Log.Write("DXProject.Current still null (poll " + polls + ", ~" + left + "s left) ...");
                }
                Thread.Sleep(500);
            }

            if (engineThreadDead)
                Log.Write("Engine thread died before DXProject.Current became ready; stopping early.");
            Log.Write("TIMEOUT: DXProject.Current remained null for ~90s; engine scene/project init did not complete.");
            DumpZxLogTail(EffectiveSavesDir(), 60);
            throw new Day0GenException("Engine scene/project init did not complete: DXProject.Current was null " +
                                       "after ~90s. See ZXLog tail above.");
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
