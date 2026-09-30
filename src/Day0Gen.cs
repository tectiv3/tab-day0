// Day0Gen — day-0 survival save generator for They Are Billions (v1.0.14).
// Mirrors the game's own survival/community-challenge start handler via runtime
// reflection only (zero compile-time game references). See SPEC.md / PLAN.md.
//
// C# 5 compatible, target net48. Must be run from the TAB install directory
// (operator places the exe there) so Assembly.Load("TheyAreBillions") resolves.
// Compile-time deps: System plus in-box System.Windows.Forms - the engine is
// WinForms (DXVision). The construct/generate/save sequence runs on this tool's
// MAIN thread: the engine has independent frame/render threads that keep
// pumping while we work (SetLevel queues InvokeOnStartFrame actions and waits
// on them - those only run if we stay OFF the engine's WinForms UI thread;
// see DispatchSequence). The legacy Control.Invoke marshal onto the engine UI
// thread is kept behind --ui-marshal for A/B testing (may deadlock in SetLevel).
//
// Subcommands: seed | save | discovery | zombie | genprobe (generator diagnostic);
// the legacy `--phase full|discovery|zombie|genprobe` form is kept as an alias
// (`full` == `seed`). Later phases imply earlier ones; genprobe is a read-only
// diagnostic, see RunGenProbe.

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
    // Logging: console + Day0Gen.log (current working directory). The only other
    // files this tool may ever write: the two save artifacts (phase full) and
    // Day0Gen-watchdog.log (watchdog abort path only - it must not share Log's
    // lock, see WatchdogAbort).
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
        // Subcommand selected as args[0] (new CLI) or via --phase (legacy alias).
        // Values: seed | save | discovery | zombie | genprobe. "full" is folded
        // into "seed" so `--phase full` keeps working byte-for-byte.
        public string Command = null;
        public string Phase = null;                     // legacy --phase selector
        public string FromSave = null;                  // save mode: source .zxsav path
        public string TabDir = @"C:\Program Files (x86)\Steam\steamapps\common\They Are Billions";
        public string SavesDir = null;                  // null => default / manager-discovered
        public bool SavesDirExplicit = false;
        public int Seed = 550040233;
        public int NCells = 256;
        public float Duration = 1.0f;
        public float Pop = 1.0f;
        // null => keep today's CC-parity default (ThemeType=None, DifficultyType
        // untouched). save mode fills these from the source SurvivalModeParams
        // unless the corresponding flag was given explicitly.
        public string Difficulty = null;
        public string Theme = null;
        public bool SeedExplicit = false, NCellsExplicit = false,
                    DurationExplicit = false, PopExplicit = false,
                    DifficultyExplicit = false, ThemeExplicit = false;
        public string Name = null;                      // default depends on subcommand
        public string ValidateSigner = null;
        public bool UiMarshal = false;                 // legacy dispatch (A/B only; may deadlock in SetLevel)

        public string DefaultSavesDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "They Are Billions", "Saves");
        }

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            // New-style subcommand: args[0] when it is not a --flag. --phase stays
            // the legacy selector and is resolved alongside it below.
            int i = 0;
            if (args.Length > 0 && args[0].Length > 0 && args[0][0] != '-')
            {
                o.Command = args[0];
                i = 1;
            }
            // Every flag takes exactly one value (consumed in pairs) except the
            // valueless --ui-marshal.
            while (i < args.Length)
            {
                string a = args[i];
                // The only valueless flag: legacy dispatch escape hatch for A/B runs.
                if (a == "--ui-marshal")
                {
                    o.UiMarshal = true;
                    i += 1;
                    continue;
                }
                bool hasVal = (i + 1) < args.Length;
                string v = hasVal ? args[i + 1] : null;
                if (!hasVal)
                    throw new Day0GenException("Missing value for " + a);
                if (a == "--phase") o.Phase = v;
                else if (a == "--from") o.FromSave = v;
                else if (a == "--tab-dir") o.TabDir = v;
                else if (a == "--saves-dir") { o.SavesDir = v; o.SavesDirExplicit = true; }
                else if (a == "--seed") { o.Seed = ParseInt(v, a); o.SeedExplicit = true; }
                else if (a == "--ncells") { o.NCells = ParseInt(v, a); o.NCellsExplicit = true; }
                else if (a == "--duration") { o.Duration = ParseFloat(v, a); o.DurationExplicit = true; }
                else if (a == "--pop") { o.Pop = ParseFloat(v, a); o.PopExplicit = true; }
                else if (a == "--difficulty") { o.Difficulty = v; o.DifficultyExplicit = true; }
                else if (a == "--theme") { o.Theme = v; o.ThemeExplicit = true; }
                else if (a == "--name") { o.Name = v; }
                else if (a == "--validate-signer") o.ValidateSigner = v;
                else throw new Day0GenException("Unknown argument: " + a);
                i += 2;
            }

            string cmd = o.Command != null ? o.Command : o.Phase;
            if (cmd == null)
                throw new Day0GenException("A subcommand is required: seed|save|discovery|zombie|genprobe " +
                                           "(legacy: --phase full|discovery|zombie|genprobe).");
            cmd = cmd.ToLowerInvariant();
            if (cmd == "full") cmd = "seed";             // backward-compatible alias
            if (cmd != "seed" && cmd != "save" && cmd != "discovery" && cmd != "zombie" && cmd != "genprobe")
                throw new Day0GenException("Unknown subcommand/phase '" + cmd +
                                           "'. Expected seed|save|discovery|zombie|genprobe.");
            o.Command = cmd;

            bool legacyPhaseFull = (o.Phase != null);
            if (cmd == "seed")
            {
                // The new subcommand requires an explicit --seed; the legacy
                // `--phase full` form keeps its historical default so the existing
                // operator launcher (--phase full --name "CC ...") keeps working.
                if (!o.SeedExplicit && !legacyPhaseFull)
                    throw new Day0GenException("seed: --seed is required (or use --phase full for the legacy default).");
                if (o.Name == null) o.Name = "Day0 " + o.Seed;
            }
            else if (cmd == "save")
            {
                if (o.FromSave == null || o.FromSave.Trim().Length == 0)
                    throw new Day0GenException("save: --from <path.zxsav> is required.");
                if (!File.Exists(o.FromSave))
                    throw new Day0GenException("save: source save not found: " + o.FromSave);
                // Deliberately NOT the source name: the tool refuses to overwrite.
                if (o.Name == null) o.Name = Path.GetFileNameWithoutExtension(o.FromSave) + " (Day0)";
            }
            else if (o.Name == null)
            {
                o.Name = "Day0";
            }

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
            TakeInto(s, dir, null);
            return s;
        }

        // exclude (optional): per-file skip filter (M3: our own artifacts in the TAB
        // dir). A throwing filter includes the file - fail closed.
        private static void TakeInto(DirSnapshot s, string dir, Predicate<string> exclude)
        {
            if (!Directory.Exists(dir)) return;
            string[] files;
            try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
            catch (Exception e)
            {
                // H2: fail closed - a partial snapshot silently disables write
                // verification exactly when it is needed most.
                throw new Day0GenException("Snapshot enumeration failed for " + dir + ": " + e.Message, e);
            }
            foreach (string f in files)
            {
                if (exclude != null)
                {
                    bool skip = false;
                    try { skip = exclude(f); } catch { }
                    if (skip) continue;
                }
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

        public void Add(string dir) { TakeInto(this, dir, null); }

        public void Add(string dir, Predicate<string> exclude) { TakeInto(this, dir, exclude); }

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
        private const string N_SAVE_WRAPPER = "_0023_003DzSV0_oCta8rEv";
        private const string N_LOADING_DIALOG = "_0023_003Dz9DPDdq9qP9lZ";
        private const string N_SAVES_FOLDER = "_0023_003DzND5ul2zfzAnWdSnC0A_003D_003D";
        private const string N_SAVE_LIST = "_0023_003DzegKkTm3kHc6FhM_EOg_003D_003D";
        private const string N_STATE_INFO = "_0023_003DzHhDw0V62_0024fqG";
        private const string N_ZXFILE_READ = "_0023_003DzyuVTytDlXbMA";
        private const string N_GENERATOR_TYPE = "_0023_003Dzyl_NPjjlA7DRfVtsRJCX1kN4BxSr";
        private const string N_GENERATE = "_0023_003DzEzgd90E_003D";
        private const string N_THEME_TABLE = "_0023_003Dz4k5FO_0024EclQhr";
        private const string N_GAMESYS_TYPE = "_0023_003DzxRcpu6e7NYzT7tGWqPjpOkc_003D";
        private const string N_SET_LEVEL = "_0023_003DzmTU4kueQctVr";
        private const string N_ADOPT_LEVEL = "_0023_003Dzf9PbDap0F6OC";
        private const string N_PRESAVE = "_0023_003DzQXHqcVh9mGZZ";
        private const string N_TABLE_LOADER_TYPE = "_0023_003Dz3Zxcp6RwVCZHa9xpeg_003D_003D";
        private const string N_TABLE_LOAD = "_0023_003DzUoK3qsRYSJTT";
        private const string N_FOG_SYSTEM_TYPE = "_0023_003DzJme8KFhmikprnkg_2CDeiQE_003D";
        // The genuine metadata FullName of the fog system type. ILSpy escapes
        // non-identifier chars with UPPERCASE hex (U+2CDE would render "_2CDE");
        // the lowercase "_2CDe" run in this name is a literal identifier
        // sequence, so Unescape is lossy for it and can never hit. Do NOT rely
        // on Unescape for this name - try this raw literal first (DiscoverAll).
        private const string N_FOG_SYSTEM_TYPE_RAW = "#=zJme8KFhmikprnkg_2CDeiQE=";
        // The fog type's immediate base (#=zsW2J3r72Cu83 : DXVision.DXSystem),
        // used to disambiguate the marker-scan fallback by base chain.
        private const string N_SYSTEM_BASE_TYPE = "#=zsW2J3r72Cu83";

        public Assembly TabAssembly;
        public Assembly DxAssembly;

        // Types
        public Type ProgramType, ManagerType, GameStateType, LevelStateType, ParamsType,
                    GameStateInfoType, GeneratorType, MapThemeType, GameSystemType,
                    TableLoaderType, DxSystemType, ZipSerializerType, DxLevelType,
                    FileGenericBase;
        // Fog system (global internal type): created by the game system's OnLoad;
        // the ZXLevelState adoption dereferences DXSystem.Get<fogsys>().
        public Type FogSystemType;
        public Type GameModeEnum, MapThemeEnum, ChallengeEnum, DifficultyEnum;

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
        public MethodInfo SaveWriterMethod;          // instance (string) -> void  [low-level native writer, no pause/PreSave]
        public MethodInfo SaveStateWrapperMethod;    // instance (string, Action, bool, bool) -> void  [game SaveState: pause + PreSave + native writer]
        public MethodInfo LoadingDialogMethod;       // instance (int, Action, Action, List<string>, bool) -> void  [engine start-game lifecycle: pause + dispose + Task create + finish-frame saveAfter]
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
        public MethodInfo GameSystemOnLoadMethod;    // instance () -> void  [creates the fog system]
        public MethodInfo DxSystemLoadMethod;        // static generic (bool) -> T
        public MethodInfo DxSystemGetMethod;         // static generic () -> T

        // ZXLevelState
        public MethodInfo AdoptLevelMethod;          // instance (DXLevel) -> void  [level adoption: SetLevel continuation]
        public MethodInfo PreSaveMethod;             // instance () -> void  [ZXLevelState PreSave: clears entities, rebuilds LevelEntities from the live registry]

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

        // C1 strategy: the engine loading dialog signature is the one contract that must
        // match exactly (notes/loading-dialog-contract.md). Validated before the reflective
        // invoke so a same-named overflow/overload can never be called with our delegate list.
        private static bool IsLoadingDialogSignature(MethodInfo m)
        {
            if (m.ReturnType != typeof(void)) return false;
            ParameterInfo[] p = m.GetParameters();
            return p.Length == 5
                && p[0].ParameterType == typeof(int)
                && p[1].ParameterType == typeof(Action)
                && p[2].ParameterType == typeof(Action)
                && p[3].ParameterType == typeof(List<string>)
                && p[4].ParameterType == typeof(bool);
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
            if (FlagMethod == null)
                // H4: no signature-scan fallback - an unverified engine method must
                // never be picked for the password machinery. Exact name or abort.
                throw new Day0GenException("Password flag method not found by exact name '" + N_FLAG +
                                           "' (build drift?) - aborting (no signature-scan fallback)");
            Found("password flag (string)->int", "exact-name", FlagMethod);

            PwdSetMethod = ManagerType.GetMethod(Unescape(N_PWD_SET),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (PwdSetMethod != null) Found("password setter (string,int,bool)->void", "exact-name", PwdSetMethod);
            PwdClearMethod = ManagerType.GetMethod(Unescape(N_PWD_CLEAR),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (PwdClearMethod != null) Found("password clearer (string,int,bool)->void", "exact-name", PwdClearMethod);
            // H4: the former blind-probe fallback (invoke every (string,int,bool)->void
            // static and see if a password appears; pick the first remaining candidate as
            // the clearer without any probe) invoked unverified engine methods with a
            // path argument. On drift we abort instead - the exact names are verified
            // for v1.0.14.
            if (PwdSetMethod == null)
                throw new Day0GenException("Password setter not found by exact name '" + N_PWD_SET +
                                           "' (build drift?) - aborting (no blind-probe fallback)");
            if (PwdClearMethod == null)
                throw new Day0GenException("Password clearer not found by exact name '" + N_PWD_CLEAR +
                                           "' (build drift?) - aborting (no blind-probe fallback)");

            // --- Save I/O --------------------------------------------------------
            SaveWriterMethod = ManagerType.GetMethod(Unescape(N_SAVEWRITER),
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (SaveWriterMethod != null) Found("game-native save writer (path)->void [writes zxsav+zxcheck]", "exact-name", SaveWriterMethod);
            else Log.Write("NOTE: game-native save writer not found by exact name; only reachable as the C1 fallback");

            // C1: the game's own save entry point - FixFileName(name), pause the engine,
            // ZXLevelState.PreSave, the native writer, unpause. This is the primary
            // writer; the low-level writer above is only a drift fallback.
            foreach (MethodInfo m in ManagerType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != Unescape(N_SAVE_WRAPPER)) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 4 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(Action)
                    && p[2].ParameterType == typeof(bool) && p[3].ParameterType == typeof(bool))
                {
                    SaveStateWrapperMethod = m;
                    break;
                }
            }
            if (SaveStateWrapperMethod != null)
                Found("game SaveState wrapper (name, callback, showWindow, preSave) [pause + PreSave + native writer]",
                      "exact-name", SaveStateWrapperMethod);
            else
                Log.Write("WARNING: game SaveState wrapper not found by exact name '" + N_SAVE_WRAPPER +
                          "'; the legacy direct-writer fallback (no pause/PreSave) will be used if reached.");

            // C1 strategy (notes/loading-dialog-contract.md): the engine's own manager
            // loading dialog runs the start-game lifecycle (pause, dispose game system,
            // Task create, finish-frame saveAfter). Exact escaped name first; a name miss
            // or signature mismatch falls back to a signature scan on the manager type.
            // Not fatal here - phase full aborts at the invoke site if it stays null.
            LoadingDialogMethod = ManagerType.GetMethod(Unescape(N_LOADING_DIALOG),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (LoadingDialogMethod != null && !IsLoadingDialogSignature(LoadingDialogMethod))
            {
                Log.Write("NOTE: manager loading dialog found by exact name '" + N_LOADING_DIALOG +
                          "' but the (int, Action, Action, List<string>, bool) signature does not match; " +
                          "scanning by signature ...");
                LoadingDialogMethod = null;
            }
            if (LoadingDialogMethod == null)
            {
                foreach (MethodInfo m in ManagerType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (IsLoadingDialogSignature(m)) { LoadingDialogMethod = m; break; }
                }
            }
            if (LoadingDialogMethod != null)
                Found("manager loading dialog (int, Action, Action, List<string>, bool) [engine start-game lifecycle]",
                      "exact-name/signature", LoadingDialogMethod);
            else
                Log.Write("WARNING: manager loading dialog not found by exact name '" + N_LOADING_DIALOG +
                          "' or by 5-arg signature; phase full will abort.");

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
            // DifficultyType is left at its default (None) unless --difficulty is given,
            // but its enum type is discovered from the property (never hardcoded) so
            // the explicit setter can validate and parse names.
            PropertyInfo difficultyProp = ParamsType.GetProperty("DifficultyType");
            if (difficultyProp == null)
                throw new Day0GenException("ZXRandomLevelParams.DifficultyType missing");
            DifficultyEnum = difficultyProp.PropertyType;
            if (!DifficultyEnum.IsEnum)
                throw new Day0GenException("ZXRandomLevelParams.DifficultyType is not an enum type: " +
                                           DifficultyEnum.FullName);
            Found("enum " + DifficultyEnum.Name + " (DifficultyType property type)",
                  "property-type", DifficultyEnum);
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

            // --- Level adoption (SetLevel continuation) -------------------------------
            // In SetLevel's new-level branch this statement sits right AFTER the
            // tolerated scene-object failure point and is skipped when that NRE
            // escapes - phase full invokes it itself (AdoptLevelIntoLevelState).
            AdoptLevelMethod = LevelStateType.GetMethod(Unescape(N_ADOPT_LEVEL),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (AdoptLevelMethod == null || AdoptLevelMethod.GetParameters().Length != 1
                || AdoptLevelMethod.GetParameters()[0].ParameterType != DxLevelType)
                throw new Day0GenException("Level adoption method " + N_ADOPT_LEVEL +
                                           "(DXLevel) not found on ZXLevelState");
            Found("ZXLevelState adopt level (SetLevel continuation)", "exact-name", AdoptLevelMethod);

            // PreSave clears the generated level's Entities + CSalvable ExtraEntities and
            // rebuilds LevelEntities from the LIVE component registry, which the tolerated
            // scene-object NRE leaves EMPTY. The save path snapshots the generated level
            // first, invokes this explicitly, then overwrites the rebuilt dictionaries
            // before the native writer runs (see SaveGeneratedState).
            PreSaveMethod = null;
            for (Type cur = LevelStateType; cur != null && PreSaveMethod == null; cur = cur.BaseType)
            {
                MethodInfo[] presaveMethods = null;
                try
                {
                    presaveMethods = cur.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (presaveMethods == null) continue;
                foreach (MethodInfo m in presaveMethods)
                {
                    if (m.Name == Unescape(N_PRESAVE) && m.GetParameters().Length == 0 &&
                        m.ReturnType == typeof(void))
                    { PreSaveMethod = m; break; }
                }
            }
            if (PreSaveMethod != null)
                Found("ZXLevelState PreSave " + N_PRESAVE + "() [clears entities; rebuilds LevelEntities]",
                      "exact-name", PreSaveMethod);
            else
                Log.Write("WARNING: ZXLevelState PreSave not found by exact name '" + N_PRESAVE +
                          "'; the save path aborts (fail closed) if reached.");

            // --- Game-system OnLoad + fog system (enter-game transition) ------------
            // The adoption above dereferences DXSystem.Get<fogsys>().LayerFog
            // (ZXLevelState.cs ~line 1606) - and that fog system is created by the
            // game system's own OnLoad (vendor/decompiled/--zxRcpu6e7NYzT7tGWqPjpOkc-.cs
            // line 1626). Our construction loads the game system DEFERRED
            // (DXSystem.Load<gamesystem>(false), mirroring the real survival click
            // handler) and the engine never ran OnLoad for the out-of-band instance
            // - phase full invokes it itself (AdoptLevelIntoLevelState). DeclaredOnly
            // selection: OnLoad is also declared on the DXSystem base chain; the game
            // system's own override is the one that creates the fog system.
            GameSystemOnLoadMethod = GameSystemType.GetMethod("OnLoad",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (GameSystemOnLoadMethod == null || GameSystemOnLoadMethod.GetParameters().Length != 0)
                throw new Day0GenException("Game system OnLoad() not found (need: instance, public, " +
                                           "0 args, declared on the game system type itself - build drift?)");
            Found("game system OnLoad() [creates the fog system; enter-game transition]",
                  "exact-name+DeclaredOnly", GameSystemOnLoadMethod);

            // Fog system type (global internal type). C1: the escaped name
            // contains a LITERAL "_2CDe" run (ILSpy only escapes with uppercase
            // hex), so Unescape turns it into U+2CDE and misses. Try the raw
            // metadata literal first, then the lossy decode, against BOTH
            // assemblies, logging every attempt. C2: only if all exact attempts
            // miss, fall through to a marker scan that actually disambiguates.
            string[] fogNames = new string[] { N_FOG_SYSTEM_TYPE_RAW, Unescape(N_FOG_SYSTEM_TYPE) };
            Assembly[] fogAsms = new Assembly[] { TabAssembly, DxAssembly };
            for (int ni = 0; ni < fogNames.Length && FogSystemType == null; ni++)
            {
                for (int ai = 0; ai < fogAsms.Length && FogSystemType == null; ai++)
                {
                    if (fogAsms[ai] == null) continue;
                    Type t = null;
                    try { t = fogAsms[ai].GetType(fogNames[ni], false); }
                    catch (Exception e)
                    {
                        Log.Write("FOG NAME TRY: " + fogAsms[ai].GetName().Name +
                                  ".GetType(\"" + fogNames[ni] + "\") threw: " +
                                  e.GetType().Name + ": " + e.Message);
                    }
                    Log.Write("FOG NAME TRY: " + fogAsms[ai].GetName().Name +
                              ".GetType(\"" + fogNames[ni] + "\") -> " +
                              (t == null ? "null" : t.FullName));
                    if (t != null) { FogSystemType = t; break; }
                }
            }
            if (FogSystemType != null)
            {
                Found("fog system type (created by game-system OnLoad)", "exact-name", FogSystemType);
            }
            else
            {
                FogSystemType = ResolveFogSystemByMarker();
                Found("fog system type (created by game-system OnLoad)", "name-marker-scan", FogSystemType);
            }

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

            foreach (MethodInfo m in DxSystemType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "Get" || !m.IsGenericMethodDefinition) continue;
                if (m.GetParameters().Length != 0) continue;
                DxSystemGetMethod = m;
                break;
            }
            if (DxSystemGetMethod == null)
                throw new Day0GenException("DXSystem.Get<T>() not found");
            Found("DXSystem.Get<T>()", "signature-scan", DxSystemGetMethod);

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
            if (SaveStateWrapperMethod == null && SaveWriterMethod == null && ZipWriteMethod == null)
                throw new Day0GenException("No save writer available: SaveState wrapper, native save writer and ZipSerializer.Write all missing");
            if (SaveStateWrapperMethod == null && SaveWriterMethod == null)
            {
                // M1: manual composition is the only remaining writer; its dependency
                // is a hard requirement (a null MethodBase would NRE opaquely later).
                if (StateInfoMethod == null)
                    throw new Day0GenException("Manual-save dependency missing: ZXGameState info-builder not found " +
                                               "(required when manual composition is the only writer)");
            }

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

        // Marker-based type locator (fallback for obfuscated names whose escaped
        // form contains non-ASCII chars): every type whose FullName carries the
        // marker. Raw collector - callers apply the top-level / non-generated /
        // base-chain filters (see ResolveFogSystemByMarker).
        private static List<Type> FindTypesByMarker(Assembly asm, string marker, List<Type> into)
        {
            if (asm == null) return into;
            foreach (Type t in SafeGetTypes(asm))
            {
                if (t == null) continue;
                string fn = null;
                try { fn = t.FullName; } catch { }
                if (fn != null && fn.IndexOf(marker, StringComparison.Ordinal) >= 0)
                    into.Add(t);
            }
            return into;
        }

        // C2: fog-type marker fallback. Scans BOTH assemblies unconditionally
        // (de-duplicated by object reference), filters to top-level,
        // non-compiler-generated types, then keeps only the type whose base
        // chain roots at the game-system base. Aborts with a full candidate dump
        // when that does not yield exactly one - the old "need exactly 1" abort
        // left drift undiagnosable, which is how the last deploy died.
        private Type ResolveFogSystemByMarker()
        {
            List<Type> markerMatches = new List<Type>();
            FindTypesByMarker(TabAssembly, "zJme8KFhmikprnkg", markerMatches);
            FindTypesByMarker(DxAssembly, "zJme8KFhmikprnkg", markerMatches);

            List<Type> unique = new List<Type>();
            foreach (Type t in markerMatches)
            {
                bool seen = false;
                foreach (Type u in unique) { if (ReferenceEquals(u, t)) { seen = true; break; } }
                if (!seen) unique.Add(t);
            }

            List<Type> filtered = new List<Type>();
            foreach (Type t in unique)
            {
                if (t.IsNested) continue;
                string fn = SafeFullName(t);
                if (fn == null) continue;
                if (fn.IndexOf("<>", StringComparison.Ordinal) >= 0) continue;
                if (fn.IndexOf("<", StringComparison.Ordinal) >= 0) continue;
                filtered.Add(t);
            }

            List<Type> systemDerived = new List<Type>();
            foreach (Type t in filtered)
            {
                if (DerivesFromSystemBase(t)) systemDerived.Add(t);
            }

            if (systemDerived.Count == 1) return systemDerived[0];

            Log.Write("FOG MARKER SCAN: raw matches=" + markerMatches.Count +
                      ", unique=" + unique.Count + ", filtered=" + filtered.Count +
                      ", system-derived=" + systemDerived.Count + ".");
            foreach (Type t in filtered)
            {
                Log.Write("FOG MARKER CANDIDATE: FullName='" + SafeFullName(t) + "', IsNested=" +
                          SafeIsNested(t) + ", base-chain=" + BaseChainString(t));
            }

            if (systemDerived.Count == 0)
                throw new Day0GenException("Fog system type not resolvable: exact names '" +
                                           N_FOG_SYSTEM_TYPE_RAW + "' / '" + Unescape(N_FOG_SYSTEM_TYPE) +
                                           "' missed and the marker scan produced no top-level, non-generated, " +
                                           "system-derived candidate (" + filtered.Count + " candidate(s) after filtering).");
            throw new Day0GenException("Fog system type not resolvable: exact names missed and the marker scan " +
                                       "produced " + systemDerived.Count + " system-derived candidate(s) (need exactly 1); " +
                                       "see the FOG MARKER CANDIDATE dump above.");
        }

        // Walks the base chain by name so the check does not depend on
        // DxSystemType, which is discovered after the fog-system resolution.
        private static bool DerivesFromSystemBase(Type t)
        {
            Type bt = t;
            for (int depth = 0; bt != null && depth < 64; depth++)
            {
                string fn = SafeFullName(bt);
                if (fn == N_SYSTEM_BASE_TYPE || fn == "DXVision.DXSystem") return true;
                try { bt = bt.BaseType; } catch { bt = null; }
            }
            return false;
        }

        private static string BaseChainString(Type t)
        {
            StringBuilder sb = new StringBuilder();
            Type bt = t;
            for (int depth = 0; bt != null && depth < 32; depth++)
            {
                if (sb.Length > 0) sb.Append(" -> ");
                string fn = SafeFullName(bt);
                sb.Append(fn == null ? "<null>" : fn);
                try { bt = bt.BaseType; } catch { bt = null; }
            }
            return sb.Length == 0 ? "(none)" : sb.ToString();
        }

        private static string SafeFullName(Type t)
        {
            try { return t.FullName; } catch { return null; }
        }

        private static bool SafeIsNested(Type t)
        {
            try { return t.IsNested; } catch { return false; }
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
        // logged right before Control.Invoke in the legacy --ui-marshal path).
        private static bool uiMarshalUsedFallback;
        private static IntPtr uiMarshalMainWindowHandle;
        private static int uiMarshalFormCount;

        // C1 strategy: state shared between the manager loading dialog's create delegate
        // (Task thread) and saveAfter delegate (engine finish frame). The ManualResetEvents
        // publish the writes to the waiting main thread (WaitOne is a full barrier),
        // which owns all post-save verification and the C4 cleanup/abort.
        private static object dialogGs, dialogParams, dialogLs, dialogSys, dialogLevel;
        private static string dialogTarget, dialogCheckPath, dialogSavesDir;
        private static ManualResetEvent dialogCreateDone, dialogSaveDone;
        private static Exception dialogCreateException, dialogSaveException;

        // Fail-closed ceiling for the start-game envelope's create+saveAfter; the 15-min
        // RunWithWatchdog is the outer backstop.
        private const int DialogCompletionTimeoutMs = 600000;

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

        // ZXMapTheme's static private theme-table field (ILSpy-escaped name). The theme
        // getter assigns this field FIRST with constructor-empty theme objects and only
        // THEN populates their properties, so an early getter call can leave it non-null
        // but half-built ("poisoned"). RebuildAndVerifyThemeTable nulls it before the
        // one post-readiness getter invocation.
        private const string N_THEME_TABLE_FIELD = "_0023_003DzFIlawjZp0PUe";

        // Game system's current-level instance field (ILSpy-escaped name; declared at
        // vendor/decompiled/--zxRcpu6e7NYzT7tGWqPjpOkc-.cs line 1273). SetLevel assigns
        // it (line 1812) BEFORE the per-entity scene-object creation whose NRE we
        // tolerate, so ReferenceEquals(field, generatedLevel) is the decisive check
        // that the engine adopted the level despite the scene-object failure.
        private const string N_CURRENT_LEVEL_FIELD = "_0023_003DzS4pP_0024s0UqLYY";

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
                if (opts.Command == "discovery") return RunDiscovery();
                if (opts.Command == "zombie") return RunZombie();
                if (opts.Command == "genprobe") return RunGenProbe();
                return RunFull();
            }
            catch (Day0GenException e)
            {
                Log.Write("ABORT: " + e.Message);
                if (e.InnerException != null)
                    Log.Write("  caused by: " + e.InnerException.GetType().Name + ": " + e.InnerException.Message);
                Log.Write("=== Day0Gen FAILED (phase " + opts.Command + ") ===");
                return 1;
            }
            catch (Exception e)
            {
                Log.Write("ABORT (unexpected): full exception chain follows.");
                LogExceptionChain("ABORT", e);
                Log.Write("=== Day0Gen FAILED (phase " + opts.Command + ") ===");
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
            return "Day0Gen <command> [options]\r\n"
                 + "\r\n"
                 + "Commands:\r\n"
                 + "  seed       generate a day-0 survival save; requires --seed\r\n"
                 + "             (legacy `--phase full` keeps its default seed)\r\n"
                 + "  save       regenerate from a source save's SurvivalModeParams; requires --from\r\n"
                 + "  discovery  reflection target discovery (read-only)\r\n"
                 + "  zombie     engine init + account/password/signer verification (read-only)\r\n"
                 + "  genprobe   interactive generator diagnostic; writes nothing besides Day0Gen.log\r\n"
                 + "\r\n"
                 + "Common options:\r\n"
                 + "  [--name S] [--ncells N] [--duration F] [--pop F]\r\n"
                 + "  [--difficulty <enum>] [--theme <enum>] [--tab-dir <dir>] [--saves-dir <dir>]\r\n"
                 + "  [--validate-signer <zxsav>] [--ui-marshal]\r\n"
                 + "  seed --name defaults to 'Day0 <seed>'; save --name to '<source> (Day0)'\r\n"
                 + "  save derives seed/ncells/duration/pop/difficulty/theme from the source;\r\n"
                 + "       explicit flags override the save-derived values\r\n"
                 + "  --difficulty/--theme take an enum name; invalid names list valid values\r\n"
                 + "\r\n"
                 + "Legacy: --phase full == seed; --phase discovery|zombie|genprobe unchanged.\r\n"
                 + "(--ui-marshal: legacy dispatch onto the engine WinForms UI thread via\r\n"
                 + " Control.Invoke - A/B testing only, may deadlock in SetLevel; default runs\r\n"
                 + " the sequence on the main tool thread)";
        }

        // ---------------------------------------------------------------------
        // save mode: derive the six generation params from the source save's
        // SurvivalModeParams (the .zxsav IS a serialized ZXGameState). Explicit CLI
        // flags override the save-derived value. A missing member or unreadable save
        // aborts (fail closed) rather than silently generating with defaults.
        // ---------------------------------------------------------------------
        private static void ApplySaveParams()
        {
            string path = Path.GetFullPath(opts.FromSave);
            Log.Write("SAVE EXTRACT: reading source save: " + path);
            object gs = ReadGameStateFromSave(path);
            if (gs == null)
                throw new Day0GenException("save: reading " + path + " returned a null ZXGameState.");

            PropertyInfo smpProp = refl.GameStateType.GetProperty("SurvivalModeParams");
            if (smpProp == null)
                throw new Day0GenException("save: ZXGameState.SurvivalModeParams property not found.");
            object p = smpProp.GetValue(gs, null);
            if (p == null)
                throw new Day0GenException("save: source save has null SurvivalModeParams (not a survival save): " + path);

            object seed = ReadRequiredParam(p, "Seed");
            object ncells = ReadRequiredParam(p, "NCells");
            object duration = ReadRequiredParam(p, "FactorGameDuration");
            object pop = ReadRequiredParam(p, "FactorZombiePopulation");
            object difficulty = ReadRequiredParam(p, "DifficultyType");
            object theme = ReadRequiredParam(p, "ThemeType");

            Log.Write("SAVE EXTRACT: source SurvivalModeParams -> Seed=" + seed + ", NCells=" + ncells +
                      ", FactorGameDuration=" + duration + ", FactorZombiePopulation=" + pop +
                      ", DifficultyType=" + difficulty + ", ThemeType=" + theme);

            if (!opts.SeedExplicit) opts.Seed = Convert.ToInt32(seed, CultureInfo.InvariantCulture);
            if (!opts.NCellsExplicit) opts.NCells = Convert.ToInt32(ncells, CultureInfo.InvariantCulture);
            if (!opts.DurationExplicit) opts.Duration = Convert.ToSingle(duration, CultureInfo.InvariantCulture);
            if (!opts.PopExplicit) opts.Pop = Convert.ToSingle(pop, CultureInfo.InvariantCulture);
            if (!opts.DifficultyExplicit) opts.Difficulty = difficulty.ToString();
            if (!opts.ThemeExplicit) opts.Theme = theme.ToString();

            Log.Write("SAVE EXTRACT: effective params after explicit-flag overrides -> Seed=" + opts.Seed +
                      ", NCells=" + opts.NCells + ", Duration=" + opts.Duration.ToString(CultureInfo.InvariantCulture) +
                      ", Pop=" + opts.Pop.ToString(CultureInfo.InvariantCulture) +
                      ", Difficulty=" + (opts.Difficulty == null ? "<default>" : opts.Difficulty) +
                      ", Theme=" + (opts.Theme == null ? "<default None>" : opts.Theme));
        }

        private static object ReadRequiredParam(object p, string name)
        {
            PropertyInfo prop = refl.ParamsType.GetProperty(name);
            if (prop == null)
                throw new Day0GenException("save: ZXRandomLevelParams." + name + " property not found.");
            object value = prop.GetValue(p, null);
            if (value == null)
                throw new Day0GenException("save: ZXRandomLevelParams." + name + " is null in the source save.");
            return value;
        }

        // Validates a user-supplied enum name (case-insensitive) against the runtime
        // enum type and returns the parsed value; an unknown name aborts with the
        // valid list instead of silently generating with a wrong/default value.
        private static object ParseEnumOrAbort(Type enumType, string value, string what)
        {
            string[] names = Enum.GetNames(enumType);
            foreach (string n in names)
            {
                if (string.Compare(n, value, StringComparison.OrdinalIgnoreCase) == 0)
                    return Enum.Parse(enumType, n, false);
            }
            throw new Day0GenException(what + ": '" + value + "' is not a valid " + enumType.Name +
                                       " value. Valid names: " + string.Join(", ", names) + ".");
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

        // Byte length of ZXLog.txt at the moment of the call. Unreadable/missing log
        // yields 0 - the post-SetLevel completion poll then scans the whole file,
        // which costs reads but keeps the run alive.
        private static long ZxLogLengthBeforeSetLevel(string savesDir)
        {
            try
            {
                string path = ZxLogPath(savesDir);
                if (!File.Exists(path)) return 0;
                long len = new FileInfo(path).Length;
                Log.Write("ZXLog length before SetLevel: " + len + " byte(s) (" + path + ")");
                return len;
            }
            catch (Exception e)
            {
                Log.Write("WARNING: cannot size ZXLog before SetLevel (" + e.GetType().Name + ": " +
                          e.Message + "); the completion poll will scan from byte 0.");
                return 0;
            }
        }

        // Reads ONLY the bytes appended to ZXLog.txt beyond `offset` (empty string
        // when nothing new). The engine keeps the log open while appending, so the
        // file is opened with ReadWrite sharing; the completion marker is pure ASCII,
        // so UTF-8 decoding of any ANSI payload still matches it.
        private static string ReadZxLogPortion(string savesDir, long offset)
        {
            string path = ZxLogPath(savesDir);
            if (offset <= 0)
            {
                try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
                catch { return ""; }
            }
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length <= offset) return "";
                    fs.Seek(offset, SeekOrigin.Begin);
                    using (StreamReader r = new StreamReader(fs))
                        return r.ReadToEnd();
                }
            }
            catch (Exception e)
            {
                Log.Write("WARNING: ZXLog incremental read failed (" + e.GetType().Name + ": " + e.Message + ").");
                return "";
            }
        }

        // Dumps every ZXLog line appended beyond `offset` (timeout diagnostics for
        // the SetLevel completion poll; same "  | " line shape as DumpZxLogTail).
        private static void DumpZxLogPortion(string savesDir, long offset)
        {
            string portion = ReadZxLogPortion(savesDir, offset);
            Log.Write("--- ZXLog new portion since SetLevel (" + ZxLogPath(savesDir) + ") ---");
            if (portion.Length == 0)
            {
                Log.Write("  | <nothing appended beyond byte " + offset + ">");
            }
            else
            {
                string[] lines = portion.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    Log.Write("  | " + lines[i].TrimEnd('\r'));
            }
            Log.Write("--- end ZXLog new portion ---");
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
            // H3: the Account.zxuser gate MUST run before ANY engine code executes.
            // The zombie manager ctor (engine thread, during the singleton poll below)
            // auto-creates + saves a fresh account when the file is missing, so a
            // post-init existence check would pass against the very file the engine
            // just wrote - and in phases without snapshots that write is invisible.
            // The check only depends on EffectiveSavesDir (manager not up yet - the
            // documented default/override path), never on engine state.
            string accountFile = Path.Combine(RootDirOf(EffectiveSavesDir()), "Account.zxuser");
            if (!File.Exists(accountFile))
                throw new Day0GenException("Account.zxuser not found at " + accountFile +
                                           " - refusing to start the engine (the manager ctor would auto-create it; " +
                                           "the GameAccount getter would create + save a fresh account file).");
            Log.Write("Account gate (pre-engine): " + accountFile + " exists.");

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

            // The account file was verified to exist BEFORE the engine started (H3),
            // so reading GameAccount now cannot create + save a fresh account.
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

        // ---------------------------------------------------------------------
        // Theme-table readiness (root-cause fix for the generator NRE).
        //
        // The theme getter (#=z4k5FO$EclQhr) populates its static dictionary in two
        // stages: it FIRST assigns the static field with constructor-empty theme
        // objects and THEN fills their properties from the table manager
        // (TableManagerDefinitions.AutoReadPropertiesInCols). Invoking the getter
        // BEFORE the engine's own "Tables Excel Read" has completed throws
        // mid-population but leaves the static field non-null - every later call
        // then returns that poisoned half-built table, themes keep null
        // NumDoomVillages, and the generator NREs (IL 0x923:
        // ZXMapTheme::get_NumDoomVillages -> DXRange::get_First on null).
        //
        // Therefore this method NEVER invokes the theme getter. It only waits
        // passively for engine readiness via the DXProject.FromID gate (non-null
        // only after the engine's OnLoad/table read), keeping the engine-death
        // watch: the headless `--phase zombie` engine dies at its modal-dialog
        // popup long before project init, which is expected there and not a theme
        // concern (that phase never touches themes). Actual theme-table rebuild +
        // verification happens in RebuildAndVerifyThemeTable() as part of the
        // dispatched sequence (phases full and genprobe), whose "Theme table OK" log
        // replaces the one this method used to emit.
        // ---------------------------------------------------------------------
        private static void CheckThemeTable()
        {
            bool useFromId = refl.DxProjectFromIdMethod != null;
            if (!useFromId &&
                (refl.DxProjectType == null ||
                 (refl.DxProjectCurrentProp == null && refl.DxProjectCurrentField == null)))
                throw new Day0GenException("Neither DXProject.FromID nor DXProject.Current was discovered; " +
                                           "cannot verify engine readiness before theme-table use.");

            string signal = useFromId ? "DXProject.FromID(" + ProjectId + ")" : "DXProject.Current";
            Log.Write("Theme-table readiness: waiting passively for " + signal + " != null " +
                      "(completes only after the engine's own table read); the theme getter is NOT " +
                      "invoked here - an early call poisons the static table.");

            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            int polls = 0;
            while (DateTime.UtcNow < deadline && !engineThreadDead)
            {
                object ready = null;
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
                    Log.Write("Engine ready (" + signal + " non-null after " + polls + " poll(s)); the " +
                              "engine's own table load has completed. The theme table will be rebuilt + " +
                              "verified before any use.");
                    return;
                }
                if (polls % 20 == 0)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalSeconds;
                    if (left < 0) left = 0;
                    Log.Write(signal + " still null (poll " + polls + ", engine " +
                              (engineThreadDead ? "dead" : "alive") + ", ~" + left + "s left) ...");
                }
                Thread.Sleep(500);
            }

            if (engineThreadDead)
            {
                Log.Write("Engine thread died before the project context became ready (expected in " +
                          "headless zombie mode); skipping theme-table readiness. No theme-table " +
                          "access was attempted, so nothing was poisoned.");
                return;
            }

            Log.Write("TIMEOUT: " + signal + " remained null for ~90s; engine init did not complete.");
            DumpZxLogTail(EffectiveSavesDir(), 60);
            throw new Day0GenException("Engine readiness (project context) was null after ~90s - cannot " +
                                       "proceed to the theme-table rebuild. See ZXLog tail above.");
        }

        // ---------------------------------------------------------------------
        // Rebuild + verify the ZXMapTheme static table. Runs inside the dispatched
        // sequence (phases full and genprobe) AFTER engine readiness, right before
        // construction / probe step 1. This is the actual root-cause fix: whatever
        // state the static field is in (null, or poisoned by an earlier premature
        // getter call), it is wiped and re-created with the engine fully ready, so
        // the internal AutoReadPropertiesInCols population must succeed. The
        // per-entry verification below is the guard that proves the fix worked.
        // ---------------------------------------------------------------------
        private static void RebuildAndVerifyThemeTable()
        {
            Log.Write("THEME REBUILD: starting (engine ready; rebuilding the ZXMapTheme static table " +
                      "from scratch).");

            // (a) locate the static private dictionary field (exact obfuscated name,
            // then the typed-static-field fallback).
            FieldInfo field = FindThemeTableField();
            if (field == null)
                throw new Day0GenException("ZXMapTheme static theme-table field not found (neither exact " +
                                           "name '" + N_THEME_TABLE_FIELD + "' nor the Dictionary<" +
                                           refl.MapThemeEnum.Name + "," + refl.MapThemeType.Name +
                                           "> static-field fallback)");

            // (b) log the current value, then wipe it (null or a poisoned half-built
            // table - either way the rebuild starts from a clean slate).
            object before = field.GetValue(null);
            Log.Write("THEME REBUILD: static field before: " +
                      (before == null
                          ? "null"
                          : "non-null with " + ThemeCount(before) + " entr(ies) (possibly poisoned)") +
                      " -> setting it to null.");
            field.SetValue(null, null);

            // (c) invoke the getter ONCE with the engine ready. Any throw here must
            // abort - proceeding with a half-built table is the original bug.
            object table;
            try
            {
                table = refl.Invoke("ZXMapTheme theme table (rebuild)", refl.ThemeTableMethod, null);
            }
            catch (Day0GenException e)
            {
                Log.Write("THEME REBUILD: getter invocation FAILED - aborting (full chain):");
                LogExceptionChain("THEME REBUILD", e);
                throw;
            }
            System.Collections.IDictionary dict = table as System.Collections.IDictionary;
            if (dict == null)
                throw new Day0GenException("Rebuilt theme table is not a dictionary: " + DescribeValue(table));
            Log.Write("THEME REBUILD: getter returned " + dict.Count + " entr(ies).");

            // (d) verify EVERY entry - the guard that the fix worked.
            PropertyInfo mtProp = FindPropertyUp(refl.MapThemeType, "MapThemeType");
            PropertyInfo nameProp = FindPropertyUp(refl.MapThemeType, "Name");
            PropertyInfo pwProp = FindPropertyUp(refl.MapThemeType, "PW");
            PropertyInfo ndvProp = FindPropertyUp(refl.MapThemeType, "NumDoomVillages");
            PropertyInfo dvsProp = FindPropertyUp(refl.MapThemeType, "DoomVillagesSize");
            PropertyInfo ntProp = FindPropertyUp(refl.MapThemeType, "NumTreasures");
            if (ndvProp == null || dvsProp == null)
                throw new Day0GenException("ZXMapTheme properties NumDoomVillages/DoomVillagesSize not " +
                                           "found - cannot verify the rebuilt theme table");

            int index = 0;
            foreach (System.Collections.DictionaryEntry ent in dict)
            {
                index++;
                object theme = ent.Value;
                if (theme == null)
                    throw new Day0GenException("Rebuilt theme table entry [" + ent.Key + "] is null");
                object ndv = SafePropGet(ndvProp, theme);
                object dvs = SafePropGet(dvsProp, theme);
                Log.Write("THEME VERIFY [" + index + "/" + dict.Count + "] key=" + ent.Key +
                          " MapThemeType=" + SafePropText(mtProp, theme) +
                          " Name=" + SafePropText(nameProp, theme) +
                          " PW=" + SafePropText(pwProp, theme) +
                          " NumDoomVillages=" + DescribeRange(ndv) +
                          " DoomVillagesSize=" + DescribeRange(dvs) +
                          " NumTreasures=" + DescribeRange(SafePropGet(ntProp, theme)));
                if (ndv == null)
                    throw new Day0GenException("Theme " + ent.Key + ": NumDoomVillages is null - theme " +
                                               "table is still poisoned/half-built after rebuild");
                if (dvs == null)
                    throw new Day0GenException("Theme " + ent.Key + ": DoomVillagesSize is null - theme " +
                                               "table is still poisoned/half-built after rebuild");
            }
            if (dict.Count < 4)
                throw new Day0GenException("Rebuilt theme table has only " + dict.Count +
                                           " entr(ies); at least 4 required (BR/AL/TM/DS)");
            Log.Write("Theme table OK (" + dict.Count + " themes, every entry verified).");
        }

        // Static theme-table field locator shared by RebuildAndVerifyThemeTable and
        // ReVerifyThemeTableQuick: exact obfuscated name first, then the unique static
        // field typed Dictionary<ZXMapThemeType, ZXMapTheme>.
        private static FieldInfo FindThemeTableField()
        {
            FieldInfo field = refl.MapThemeType.GetField(GameReflector.Unescape(N_THEME_TABLE_FIELD),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field;
            Type expected = typeof(Dictionary<,>).MakeGenericType(refl.MapThemeEnum, refl.MapThemeType);
            foreach (FieldInfo f in refl.MapThemeType.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType == expected)
                {
                    Log.Write("THEME: exact-name field lookup missed; fallback found static field '" +
                              f.Name + "' : " + f.FieldType.FullName);
                    return f;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // H5: cheap re-verification of the ZXMapTheme static table, called
        // immediately BEFORE the generator invoke (full + genprobe). Between the
        // rebuild (pre-construction) and the generator, seconds of engine-side
        // scene activity pass; this closes the re-poisoning window to microseconds.
        // It reads the static FIELD directly - the lazy getter would only rebuild on
        // null and reading the field invokes no engine code at all - and aborts on
        // null table, <4 entries, or any entry with a null NumDoomVillages (the
        // exact poisoned shape that NREs the generator).
        // ---------------------------------------------------------------------
        private static void ReVerifyThemeTableQuick()
        {
            FieldInfo field = FindThemeTableField();
            if (field == null)
                throw new Day0GenException("ReVerifyThemeTableQuick: theme-table field not found");
            object table = field.GetValue(null);
            System.Collections.IDictionary dict = table as System.Collections.IDictionary;
            if (dict == null)
                throw new Day0GenException("ReVerifyThemeTableQuick: theme table is null or not a dictionary (" +
                                           DescribeValue(table) + ") - re-poisoned after rebuild; aborting before generator");
            if (dict.Count < 4)
                throw new Day0GenException("ReVerifyThemeTableQuick: theme table has only " + dict.Count +
                                           " entr(ies) (>=4 required) - re-poisoned after rebuild; aborting before generator");
            PropertyInfo ndvProp = FindPropertyUp(refl.MapThemeType, "NumDoomVillages");
            if (ndvProp == null)
                throw new Day0GenException("ReVerifyThemeTableQuick: ZXMapTheme.NumDoomVillages property not found");
            foreach (System.Collections.DictionaryEntry ent in dict)
            {
                object theme = ent.Value;
                if (theme == null)
                    throw new Day0GenException("ReVerifyThemeTableQuick: theme entry [" + ent.Key +
                                               "] is null - re-poisoned after rebuild; aborting before generator");
                if (SafePropGet(ndvProp, theme) == null)
                    throw new Day0GenException("ReVerifyThemeTableQuick: theme " + ent.Key +
                                               " has null NumDoomVillages - re-poisoned after rebuild; aborting before generator");
            }
            Log.Write("THEME QUICK RE-VERIFY OK (" + dict.Count + " entr(ies), every NumDoomVillages non-null).");
        }

        // ---------------------------------------------------------------------
        // Entity-default-params readiness gate (called immediately before the
        // generator invoke).
        //
        // ZXEntity.#=zthu8vuk=() reads manager.<entityDefaultParams>[Template.Name]
        // whenever that dictionary is non-empty; CreateInstance runs it for every
        // entity the generator instantiates. The manager fills the dictionary in
        // #=zCr_j9C0uHkPXZ$SDGA==, a PostMethods_OnStartFrame action that runs AFTER
        // CurrentProject = DXProject.LoadFromFile(...). DXProject.FromID becomes
        // non-null at the LoadFromFile boundary, so WaitForProjectContext can pass
        // while the dictionary is still filling; invoking the generator then races
        // the engine and throws KeyNotFoundException inside CreateInstance. This
        // gate waits for the dictionary to hold every ZXEntity template.
        //
        // A discovery failure is fail-open (log + return): the generator still
        // fails closed on its own if the race actually hits.
        // ---------------------------------------------------------------------
        private static void WaitForEntityDefaultParamsGate()
        {
            Type edpType;
            FieldInfo dictField;
            Type zxEntityType;
            object templatesValue;
            object project;
            int expected;
            string ccName;

            try
            {
                edpType = FindTypeAnyOrder("ZX.ZXEntityDefaultParams", refl.TabAssembly);
                if (edpType == null)
                    edpType = FindTypeAnyOrder("ZX.EntityDefaultParams", refl.TabAssembly);
                if (edpType == null)
                    edpType = FindTypeByNameSuffix("ZXEntityDefaultParams");
                if (edpType == null)
                {
                    Log.Write("ENTITY PARAMS GATE: skipped (ZXEntityDefaultParams type not found) - " +
                              "cannot verify manager entity-default-params readiness");
                    return;
                }

                dictField = FindEntityDefaultParamsDictField(refl.ManagerType, edpType);
                if (dictField == null)
                {
                    Log.Write("ENTITY PARAMS GATE: skipped (manager Dictionary<string,ZXEntityDefaultParams> " +
                              "instance field not found on " +
                              (refl.ManagerType == null ? "null" : refl.ManagerType.FullName) + ") - " +
                              "cannot verify manager entity-default-params readiness");
                    return;
                }
                Log.Write("ENTITY PARAMS GATE: manager dict field '" + dictField.Name + "' : " +
                          dictField.FieldType.FullName);

                zxEntityType = FindTypeAnyOrder("ZX.Entities.ZXEntity", refl.TabAssembly);
                if (zxEntityType == null)
                    zxEntityType = FindTypeByNameSuffix("ZXEntity");
                if (zxEntityType == null)
                {
                    Log.Write("ENTITY PARAMS GATE: skipped (ZX.Entities.ZXEntity type not found) - " +
                              "cannot verify manager entity-default-params readiness");
                    return;
                }

                if (managerInstance == null)
                {
                    Log.Write("ENTITY PARAMS GATE: skipped (managerInstance is null) - " +
                              "cannot verify manager entity-default-params readiness");
                    return;
                }

                // FromID is populated by LoadFromFile and non-null by now (WaitForProjectContext);
                // Current is the weaker fallback if FromID was never discovered.
                if (refl.DxProjectFromIdMethod != null)
                    project = refl.DxProjectFromIdMethod.Invoke(null, new object[] { ProjectId });
                else
                    project = ReadDxProjectCurrent();
                if (project == null)
                {
                    Log.Write("ENTITY PARAMS GATE: skipped (DXProject.FromID(" + ProjectId + ") returned null) - " +
                              "cannot verify manager entity-default-params readiness");
                    return;
                }

                templatesValue = ReadEntityTemplatesValue(project);
                if (templatesValue == null)
                {
                    Log.Write("ENTITY PARAMS GATE: skipped (DXProject.EntityTemplates member not found or null on " +
                              project.GetType().FullName + ") - cannot verify manager entity-default-params readiness");
                    return;
                }
                System.Collections.IDictionary templates = templatesValue as System.Collections.IDictionary;
                if (templates == null)
                {
                    Log.Write("ENTITY PARAMS GATE: skipped (EntityTemplates is not IDictionary: " +
                              DescribeValue(templatesValue) + ") - cannot verify manager entity-default-params readiness");
                    return;
                }

                // Exact expected count: the manager adds one dict entry per
                // EntityTemplates.Values entry whose Entity is a ZXEntity. The reserved
                // CommandCenter template name is probed as a secondary key check when
                // resolvable.
                expected = 0;
                foreach (System.Collections.DictionaryEntry ent in templates)
                {
                    bool isZxEntity = false;
                    try
                    {
                        object template = ent.Value;
                        if (template != null)
                        {
                            PropertyInfo ep = FindPropertyUp(template.GetType(), "Entity");
                            object entity = ep == null ? null : ep.GetValue(template, null);
                            isZxEntity = entity != null && zxEntityType.IsInstanceOfType(entity);
                        }
                    }
                    catch { isZxEntity = false; }
                    if (isZxEntity) expected++;
                }

                ccName = null;
                try
                {
                    if (templates.Contains(GenProbeCommandCenterTemplateId))
                    {
                        object ccTemplate = templates[GenProbeCommandCenterTemplateId];
                        if (ccTemplate != null)
                        {
                            PropertyInfo np = FindPropertyUp(ccTemplate.GetType(), "Name");
                            object name = np == null ? null : np.GetValue(ccTemplate, null);
                            ccName = name as string;
                        }
                    }
                }
                catch { ccName = null; }
            }
            catch (Exception e)
            {
                Log.Write("ENTITY PARAMS GATE: skipped (discovery threw: " + DescribeException(e) + ") - " +
                          "cannot verify manager entity-default-params readiness");
                return;
            }

            bool requireCc = !string.IsNullOrEmpty(ccName);
            Log.Write("ENTITY PARAMS GATE: expected=" + expected + " ZXEntity template(s)" +
                      (requireCc ? ", CommandCenter template name resolved" : ", CommandCenter template name unresolved") +
                      " (manager dict '" + dictField.Name + "')");

            // The dict is mutated by the engine thread; wrap EVERY read (Count,
            // Contains) so a concurrent-mutation exception never aborts the gate.
            DateTime deadline = DateTime.UtcNow.AddSeconds(120);
            int polls = 0;
            int lastCount = -1;
            bool lastCc = false;
            bool probeLogged = false;
            while (DateTime.UtcNow < deadline && !engineThreadDead)
            {
                int count = -1;
                bool ccPresent = !requireCc;
                try
                {
                    object dictObj = dictField.GetValue(managerInstance);
                    System.Collections.IDictionary d = dictObj as System.Collections.IDictionary;
                    if (d != null)
                    {
                        count = d.Count;
                        if (requireCc) ccPresent = d.Contains(ccName);
                    }
                    else if (!probeLogged)
                    {
                        Log.Write("ENTITY PARAMS GATE: manager dict is not IDictionary (" +
                                  DescribeValue(dictObj) + "); will keep polling");
                        probeLogged = true;
                    }
                }
                catch (Exception e)
                {
                    if (!probeLogged)
                    {
                        Log.Write("ENTITY PARAMS GATE: dict probe threw (engine thread still mutating; " +
                                  "will keep polling): " + DescribeException(e));
                        probeLogged = true;
                    }
                }

                polls++;
                lastCount = count;
                lastCc = ccPresent;
                if (count >= expected && ccPresent)
                {
                    Log.Write("ENTITY PARAMS GATE: ready after " + polls + " poll(s): dict count=" + count +
                              " / expected=" + expected +
                              (requireCc ? ", CommandCenter key present=true" : "") + ".");
                    return;
                }
                if (polls % 10 == 0)
                {
                    Log.Write("ENTITY PARAMS GATE: dict count=" + count + " / expected=" + expected +
                              " (poll " + polls + ")" +
                              (requireCc ? ", CommandCenter key present=" + ccPresent.ToString().ToLowerInvariant() : ""));
                }
                Thread.Sleep(500);
            }

            if (engineThreadDead)
                Log.Write("ENTITY PARAMS GATE: engine thread died while waiting; aborting before generator.");
            throw new Day0GenException("Entity-default-params gate timed out after 120s: manager dict count=" +
                                       lastCount + " / expected=" + expected +
                                       (requireCc ? ", CommandCenter key present=" + lastCc.ToString().ToLowerInvariant() : "") +
                                       " - the generator would throw KeyNotFoundException in DXEntity.CreateInstance; aborting.");
        }

        // ---------------------------------------------------------------------
        // Start-screen settle gate (called immediately before the generator invoke).
        //
        // The engine runs its STARTUP fade-to-start-screen transition asynchronously;
        // its onFinish callback then drives ChangeScene -> ZXSystem_StartScreen -
        // Load/ShowScene, whose teardown tears down the game level and wipes every
        // entity. If generation/SetLevel runs while that transition is still pending,
        // the onFinish fires AFTER the level load and wipes it before the save. This
        // gate waits for the transition's terminal ZXLog marker so no pending scene
        // change remains. It is fail-open: the post-save CC assertion is the
        // fail-closed backstop, so a timeout must never abort. See
        // notes/missing-command-center.md (start-screen teardown root cause).
        // ---------------------------------------------------------------------
        private const string StartScreenSettledMarker = "ZXSystem_StartScreen - ShowSceneSuccess";

        private static void WaitForStartScreenSettled(string savesDir)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            int polls = 0;
            while (DateTime.UtcNow < deadline)
            {
                polls++;
                // Whole-file scan (offset 0): the engine truncates ZXLog on start
                // ("Log Start"), and the transition may already have completed during
                // an earlier readiness wait, so a new-bytes-only scan would miss it.
                string whole = ReadZxLogPortion(savesDir, 0);
                if (whole.IndexOf(StartScreenSettledMarker, StringComparison.Ordinal) >= 0)
                {
                    Log.Write("STARTSCREEN GATE: engine settled on the start screen after " +
                              polls + " poll(s).");
                    return;
                }
                if (engineThreadDead)
                {
                    Log.Write("STARTSCREEN GATE: engine thread died while waiting; proceeding without " +
                              "start-screen settle.");
                    return;
                }
                if (polls % 10 == 0)
                    Log.Write("STARTSCREEN GATE: waiting for '" + StartScreenSettledMarker +
                              "' (poll " + polls + ")");
                Thread.Sleep(500);
            }
            // Timeout (or unreadable ZXLog, which the read helper reports as empty):
            // warn and continue - the read-back CC assertion fails closed if the level
            // was actually wiped.
            Log.Write("STARTSCREEN GATE: marker not seen after 90s (engine may still be transitioning); " +
                      "proceeding - the save CC assertion will catch an empty level");
        }

        // Locates the manager's entity-default-params dictionary: the single instance
        // field typed Dictionary<string, ZXEntityDefaultParams>. Falls back to matching
        // any dictionary value type whose simple name ends with ZXEntityDefaultParams so
        // a name-resolution miss does not hide the field.
        private static FieldInfo FindEntityDefaultParamsDictField(Type managerType, Type edpType)
        {
            if (managerType == null) return null;
            List<FieldInfo> matches = new List<FieldInfo>();
            for (Type cur = managerType; cur != null; cur = cur.BaseType)
            {
                FieldInfo[] fields;
                try
                {
                    fields = cur.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { break; }
                foreach (FieldInfo f in fields)
                {
                    Type ft;
                    try { ft = f.FieldType; } catch { continue; }
                    if (ft == null || !ft.IsGenericType) continue;
                    try { if (ft.GetGenericTypeDefinition() != typeof(Dictionary<,>)) continue; }
                    catch { continue; }
                    Type[] args;
                    try { args = ft.GetGenericArguments(); } catch { continue; }
                    if (args == null || args.Length != 2) continue;
                    bool match = edpType != null && args[1] == edpType;
                    if (!match)
                    {
                        string vn = null;
                        try { vn = args[1].Name; } catch { }
                        if (vn != null && vn.EndsWith("ZXEntityDefaultParams", StringComparison.Ordinal))
                            match = true;
                    }
                    if (match) matches.Add(f);
                }
            }
            if (matches.Count != 1)
            {
                if (matches.Count > 1)
                    Log.Write("ENTITY PARAMS GATE: " + matches.Count +
                              " candidate manager entity-default-params dict fields; discovery ambiguous.");
                return null;
            }
            return matches[0];
        }

        // Fallback type locator by simple-name suffix across both assemblies; used only
        // when the exact full name misses. Returns null when not unique so callers can
        // fail open.
        private static Type FindTypeByNameSuffix(string simpleNameSuffix)
        {
            List<Type> matches = new List<Type>();
            AddTypeSuffixMatches(GameReflector.SafeGetTypes(refl.TabAssembly), simpleNameSuffix, matches);
            AddTypeSuffixMatches(GameReflector.SafeGetTypes(refl.DxAssembly), simpleNameSuffix, matches);
            if (matches.Count >= 2)
            {
                List<Type> unique = new List<Type>();
                foreach (Type m in matches)
                {
                    bool seen = false;
                    foreach (Type u in unique)
                    {
                        if (ReferenceEquals(u, m)) { seen = true; break; }
                    }
                    if (!seen) unique.Add(m);
                }
                matches = unique;
            }
            return matches.Count == 1 ? matches[0] : null;
        }

        private static void AddTypeSuffixMatches(Type[] types, string suffix, List<Type> into)
        {
            if (types == null) return;
            foreach (Type t in types)
            {
                if (t == null) continue;
                string n = null;
                try { n = t.Name; } catch { }
                if (n == null || !n.EndsWith(suffix, StringComparison.Ordinal)) continue;
                bool nested = false;
                try { nested = t.IsNested; } catch { nested = false; }
                if (nested) continue;
                string fn = null;
                try { fn = t.FullName; } catch { }
                if (fn != null && (fn.IndexOf("<", StringComparison.Ordinal) >= 0 ||
                                   fn.IndexOf(">", StringComparison.Ordinal) >= 0)) continue;
                into.Add(t);
            }
        }

        private static object ReadEntityTemplatesValue(object project)
        {
            if (project == null) return null;
            try
            {
                Type t = project.GetType();
                PropertyInfo p = FindPropertyUp(t, "EntityTemplates");
                if (p != null) return p.GetValue(project, null);
                FieldInfo f = FindFieldUp(t, "EntityTemplates");
                if (f != null) return f.GetValue(project);
            }
            catch (Exception e)
            {
                Log.Write("ENTITY PARAMS GATE: EntityTemplates read threw: " + DescribeException(e));
            }
            return null;
        }

        // Null-safe property read for verification logging: a missing property or a
        // throwing getter reads as null (verification treats null as failure).
        private static object SafePropGet(PropertyInfo p, object o)
        {
            try { return p == null ? null : p.GetValue(o, null); }
            catch { return null; }
        }

        private static string SafePropText(PropertyInfo p, object o)
        {
            object v = SafePropGet(p, o);
            return v == null ? "?" : v.ToString();
        }

        // A DXRange<int> rendered as "First..Last" via its First/Last properties.
        private static string DescribeRange(object range)
        {
            if (range == null) return "null";
            PropertyInfo first = FindPropertyUp(range.GetType(), "First");
            PropertyInfo last = FindPropertyUp(range.GetType(), "Last");
            object f = SafePropGet(first, range);
            object l = SafePropGet(last, range);
            return (f == null ? "?" : f.ToString()) + ".." + (l == null ? "?" : l.ToString());
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
                // H4: "works" means the full cycle - the clear must succeed AND leave
                // the password empty again. A probe that leaves engine state dirty has
                // not passed; a swallowed clear failure would poison the real run.
                refl.Invoke("password generator(clear)", refl.PwdClearMethod, null, path, flag, true);
                string pwdAfter = (string)refl.GetProp("ZipSerializer.Password (after clear)",
                                                       refl.ZipPasswordProp, zip);
                if (!string.IsNullOrEmpty(pwdAfter))
                {
                    Log.Write("ZipSerializer.Password NOT empty after clear (len " + pwdAfter.Length +
                              ") - probe failed.");
                    return false;
                }
                Log.Write("Password machinery verified: set -> non-empty password, clear -> empty again.");
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
        // sequence runs through the same dispatch as phase full, each step
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

            ZombieInit();               // includes account gate + passive engine-readiness wait
            ProbePasswordMachinery();
            WaitForProjectContext();

            Log.Write("GENPROBE: starting probe sequence; this phase writes " +
                      "nothing besides Day0Gen.log (no snapshots, no save artifacts).");
            try
            {
                DispatchSequence("genprobe sequence",
                    "no artifacts are written by the genprobe phase; just collect Day0Gen.log.",
                    delegate
                    {
                        // Root-cause fix: wipe + rebuild + verify the ZXMapTheme static
                        // table BEFORE step 1 (the theme pick reads that table).
                        RebuildAndVerifyThemeTable();
                        GenProbeSequence();
                    });
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
            Log.Write("GENPROBE: sequence starting (thread id=" +
                      Thread.CurrentThread.ManagedThreadId + ").");
            // NOTE: steps 1/1b run AFTER RebuildAndVerifyThemeTable (see the dispatch
            // delegate in RunGenProbe), so the theme table they read is the freshly
            // rebuilt + verified one, never a poisoned half-built table.
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
        // FirstChanceException diagnostic (shared by the genprobe and full
        // phases). The generator NRE is stackless by the time our catch block
        // sees it; at throw time the CLR may still have frames.
        // Filter: NullReferenceException / KeyNotFoundException / IndexOutOfRangeException
        // whose stack mentions the generator class, the game-system class
        // (zxRcpu6e7NYzT7tGWqPjpOkc=), the DXVision map types, or the level-state /
        // fog-system / DXSystem frames of the adopt + OnLoad chain; stackless
        // exceptions of those types are logged too (marked) - the stackless NRE is
        // precisely the failure under investigation. Tiny + fully guarded: this runs
        // on EVERY first-chance exception in the process until unregistered.
        // ---------------------------------------------------------------------
        private static EventHandler<FirstChanceExceptionEventArgs> genProbeFce;
        private static int genProbeFceLogged;
        private const int GenProbeFceLogLimit = 150;

        private static void RegisterFirstChanceHandler()
        {
            // At most one live handler: a second += would log every event twice.
            if (genProbeFce != null) return;
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
                        && st.IndexOf("zxRcpu6e7NYzT7tGWqPjpOkc=", StringComparison.Ordinal) < 0
                        && st.IndexOf("DXNoyseLayer", StringComparison.Ordinal) < 0
                        && st.IndexOf("DXWorldGrid", StringComparison.Ordinal) < 0
                        && st.IndexOf("ZXMapDrawer", StringComparison.Ordinal) < 0
                        && st.IndexOf("ZXLevelState", StringComparison.Ordinal) < 0
                        && st.IndexOf("zJme8KFhmikprnkg", StringComparison.Ordinal) < 0
                        && st.IndexOf("DXSystem", StringComparison.Ordinal) < 0)
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
                      "generator + game-system + DXVision map-type + level-state/fog/DXSystem stacks).");
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
        // 1 dictionary arg) - step 1's static-extension scan cannot see it, so this
        // step drives the instance method directly with a reflected dictionary.
        // Runs AFTER RebuildAndVerifyThemeTable, so `table` here is the verified
        // rebuilt one.
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

            // source.ToDictionary(k => table[k], k => table[k].PW) via reflection.
            // ZXMapTheme.PW is `public int PW`, so the generator's ToDictionary
            // produces Dictionary<ZXMapTheme,int> (NOT float - the earlier float
            // reading of the MemberRef was wrong and made the closed parameter
            // reject our dictionary). Key = the theme OBJECT, value = its PW as int.
            Type wdictType = typeof(Dictionary<,>).MakeGenericType(refl.MapThemeType, typeof(int));
            object wdict = Activator.CreateInstance(wdictType);
            System.Collections.IDictionary wdictIface = (System.Collections.IDictionary)wdict;
            foreach (object k in keyList)
            {
                object theme = dict[k];
                if (theme == null)
                    throw new Day0GenException("theme table[" + k + "] is null");
                int pw = Convert.ToInt32(pwProp.GetValue(theme, null), CultureInfo.InvariantCulture);
                wdictIface.Add(theme, pw);
                Log.Write("GENPROBE step1b: dict.Add(table[" + k + "]) key=" + DescribeValue(theme) +
                          " PW=" + pw);
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
            // H5: same immediate pre-generator re-verification as the full phase - the
            // probe steps 1-7 took time and touched engine state after the rebuild.
            ReVerifyThemeTableQuick();
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
        // Engine UI-thread marshal target (legacy --ui-marshal path only). The
        // engine (DXVision) is WinForms: it pumps messages on its own STA thread;
        // this marshal runs our sequence on that thread (in the real game such
        // sequences live in click handlers there) - the dispatch now known to
        // deadlock in SetLevel, hence kept only for A/B testing. Preference: the
        // Application.OpenForms form whose handle equals the process
        // MainWindowHandle; fallback Control.FromHandle(MainWindowHandle).
        // Returns null when neither yields a Control - the caller then runs
        // inline on its own thread. Fills the uiMarshal* diagnostics fields for
        // the pre-Invoke log.
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
        // LEGACY engine UI-thread dispatch (--ui-marshal only). Runs a whole
        // sequence on the engine UI thread via Control.Invoke on the marshal
        // target (FindEngineUiMarshalTarget), with a 15-min watchdog that aborts
        // the process when the engine loop stops pumping messages. KNOWN FAILURE
        // MODE: SetLevel (#=zmTU4kueQctVr, vendor/decompiled/
        // --zxRcpu6e7NYzT7tGWqPjpOkc-.cs line 1685) queues InvokeOnStartFrame and
        // blocks in WaitOne until the engine frame loop runs it - with our
        // delegate occupying the WinForms UI thread, the engine's pending scene
        // change (ZXGameState.Set) and that WaitOne deadlock each other
        // (observed: SetLevel hung at ~3% CPU until the watchdog killed it).
        // Falls back to the current thread when no marshal target exists.
        // stallNote is logged by the watchdog for operator follow-up.
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
            // M4: volatile completion flag - locals cannot be volatile in C#5, so the
            // bool lives in a holder with a volatile field.
            VolatileBool marshalFinished = new VolatileBool();
            Thread uiWatchdog = new Thread(delegate()
            {
                DateTime armedAt = DateTime.UtcNow;
                while (!marshalFinished.Value)
                {
                    Thread.Sleep(1000);
                    if (marshalFinished.Value) return;
                    if (DateTime.UtcNow - armedAt >= TimeSpan.FromMinutes(15))
                    {
                        // H6: never Log.Write from a watchdog - a thread blocked in
                        // Log.Write would block the watchdog on the same lock and the
                        // exit below would never run.
                        WatchdogAbort("UI-thread marshal watchdog fired - engine loop not pumping " +
                                      "(legacy dispatch; known SetLevel WaitOne deadlock mode); " +
                                      "aborting process; " + stallNote);
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
                marshalFinished.Value = true;
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
        // Dispatch entry for the construct/generate/save (full) and probe
        // (genprobe) sequences. Default: run body DIRECTLY on the current main
        // tool thread - the engine has independent frame/render threads (ZXLog
        // logs RenderFrame while our thread executes), so SetLevel's queued
        // InvokeOnStartFrame actions execute and its WaitOne signals. This is
        // TABSAT's arrangement: the engine animates on its threads while the
        // reflector works on its own. --ui-marshal forces the legacy
        // Control.Invoke path for A/B testing (may deadlock in SetLevel, see
        // RunOnEngineUiThread).
        // ---------------------------------------------------------------------
        private static void DispatchSequence(string sequenceName, string stallNote, MethodInvoker body)
        {
            if (opts.UiMarshal)
            {
                Log.Write("dispatch: UI-marshal (legacy, may deadlock in SetLevel).");
                RunOnEngineUiThread(sequenceName, stallNote, body);
                return;
            }
            Log.Write("dispatch: main thread (engine threads pump freely).");
            LogEngineUiThreadDiagnostics();
            RunWithWatchdog(sequenceName, stallNote, body);
        }

        // 15-min watchdog around a sequence executed on the current (main tool)
        // thread: the sequence can block indefinitely if the engine frame loop
        // stops running queued start-frame actions (SetLevel's WaitOne never
        // signals); Environment.Exit(2) keeps the process from hanging forever.
        // stallNote tells the operator what to review afterwards.
        private static void RunWithWatchdog(string sequenceName, string stallNote, MethodInvoker body)
        {
            // M4: volatile completion flag (holder - C#5 locals cannot be volatile).
            VolatileBool finished = new VolatileBool();
            Thread watchdog = new Thread(delegate()
            {
                DateTime armedAt = DateTime.UtcNow;
                while (!finished.Value)
                {
                    Thread.Sleep(1000);
                    if (finished.Value) return;
                    if (DateTime.UtcNow - armedAt >= TimeSpan.FromMinutes(15))
                    {
                        // H6: lock-free abort path (see WatchdogAbort).
                        WatchdogAbort("main-thread watchdog fired - " + sequenceName + " stalled for 15 min " +
                                      "(likely SetLevel InvokeOnStartFrame/WaitOne never signaled: engine frame " +
                                      "loop not running); aborting process; " + stallNote);
                    }
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();
            try { body(); }
            finally { finished.Value = true; }
        }

        // M4: completion flag holder - `volatile` guarantees the watchdog thread
        // observes the completion write instead of hoisting the read out of its loop.
        private sealed class VolatileBool
        {
            public volatile bool Value;
        }

        // H6: watchdog abort path that shares NOTHING with Log (no gate lock, no
        // Day0Gen.log writer). A thread blocked forever inside Log.Write (full
        // console pipe) must not be able to block the watchdog: the durable record
        // goes to a separate file first, then stderr, then the process exits.
        private static void WatchdogAbort(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                          "  " + message;
            try
            {
                File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "Day0Gen-watchdog.log"),
                                   line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
            try { Console.Error.WriteLine(line); } catch { }
            Environment.Exit(2);
        }

        // Reference-only snapshot of the engine WinForms topology, logged before
        // direct (main-thread) execution. Not used for the dispatch decision -
        // kept so log runs can be correlated with the form/handle layout the
        // legacy marshal targeted.
        private static void LogEngineUiThreadDiagnostics()
        {
            try
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    self.Refresh();
                    Log.Write("engine-UI info (not used for dispatch): MainWindowHandle=" +
                              self.MainWindowHandle + ".");
                }
                List<Form> formList = new List<Form>();
                foreach (Form f in Application.OpenForms) formList.Add(f);
                Log.Write("engine-UI info (not used for dispatch): OpenForms count=" +
                          formList.Count + ".");
                for (int i = 0; i < formList.Count; i++)
                {
                    Log.Write("engine-UI info (not used for dispatch): form[" + i + "] Name='" +
                              SafeControlName(formList[i]) + "' Text='" + SafeControlText(formList[i]) + "'.");
                }
            }
            catch (Exception e)
            {
                Log.Write("engine-UI info (not used for dispatch): snapshot threw: " +
                          e.GetType().Name + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------------
        // Phase: full — construction + generation + save + verify
        // ---------------------------------------------------------------------

        // Full-path equality helper for the after-run allow-list (H1): normalize both
        // sides, compare case-insensitively (Windows filesystem semantics).
        private static bool SameFullPath(string a, string b)
        {
            string fa = Path.GetFullPath(a).TrimEnd('\\');
            string fb = Path.GetFullPath(b).TrimEnd('\\');
            return string.Compare(fa, fb, StringComparison.OrdinalIgnoreCase) == 0;
        }

        // M3: our own deployed artifacts in the TAB dir, excluded from the before/
        // after snapshots. Everything else in the TAB dir is in scope - any engine
        // write there shows up as an unexpected change and aborts the run.
        private static readonly string[] OwnTabDirArtifacts = new string[]
        {
            "Day0Gen.exe", "Day0Gen.exe.config", "Day0Gen.pdb", "Day0Gen.log", "Day0Gen-watchdog.log"
        };

        private static bool IsOwnTabDirArtifact(string path)
        {
            string name = Path.GetFileName(path);
            foreach (string a in OwnTabDirArtifacts)
            {
                if (string.Compare(name, a, StringComparison.OrdinalIgnoreCase) == 0) return true;
            }
            return name.StartsWith("run-day0-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        }

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

            Log.Write("Snapshot BEFORE (saves dir + parent + TAB dir): " + savesDir + " ; " + rootDir +
                      " ; " + opts.TabDir);
            DirSnapshot before = DirSnapshot.Take(savesDir);
            before.Add(rootDir);
            // M3: the TAB install dir is our CWD - an engine crash log/minidump written
            // there must be visible to the diff. Our own artifacts are excluded
            // (Day0Gen.log is appended to by this very tool; exe/config/pdb and the
            // run-day0-*.bat launchers are static operator files).
            before.Add(opts.TabDir, IsOwnTabDirArtifact);

            refl = new GameReflector();
            refl.LoadAssemblies();
            refl.DiscoverAll(false);
            ZombieInit();               // includes account gate + passive engine-readiness wait
            ProbePasswordMachinery();
            // save mode's source-save read happens later, inside the paused-engine
            // envelope (see CreateGameStateAndLevel): deserializing a foreign
            // ZXGameState during engine startup races the start-screen scene load and
            // NREs the loading-screen render (DXProjectImage.get_ImageArea).

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

            // FCE diagnostics for phase full too (same handler, filter and 150-event
            // cap as genprobe): registered HERE, right before the sequence, so the
            // event budget is spent on construct/generate/save rather than on engine
            // start-up noise.
            RegisterFirstChanceHandler();

            try
            {
                // ---- run the construct/generate/save sequence via the standard dispatch
                // (main thread by default: the engine's own frame/render threads keep
                // pumping, which SetLevel's InvokeOnStartFrame + WaitOne depends on;
                // --ui-marshal forces the legacy Control.Invoke path for A/B runs).
                DispatchSequence("construct/generate/save",
                    "if partial files exist, delete '" + target + "' / '" + checkPath + "' manually after review.",
                    delegate
                    {
                        // Root-cause fix: wipe + rebuild + verify the ZXMapTheme static table
                        // BEFORE construction/generation (any earlier getter call may have
                        // poisoned it; the generator reads NumDoomVillages from it).
                        RebuildAndVerifyThemeTable();
                        RunConstructGenerateSave(target, checkPath, effectiveSavesDir);
                    });

                // ---- after snapshot -------------------------------------------------------
                Log.Write("Snapshot AFTER ...");
                DirSnapshot after = DirSnapshot.Take(effectiveSavesDir);
                after.Add(RootDirOf(effectiveSavesDir));
                after.Add(opts.TabDir, IsOwnTabDirArtifact);
                if (string.Compare(effectiveSavesDir, savesDir, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    after.Add(savesDir);
                    after.Add(rootDir);
                }
                List<string> changes = DirSnapshot.Diff(before, after);
                // H1: allow-list compares FULL absolute paths (case-insensitive) - exactly
                // the two artifacts plus the engine's ZXLog.txt in the saves root. A file
                // merely NAMED like our target in any other (sub)directory is unexpected.
                string zxLogFull = Path.GetFullPath(ZxLogPath(effectiveSavesDir));
                // The engine's render-thread NRE writes its own crash pair next to our
                // target (<stem>_Crash.zxsav/.zxcheck). It is an engine side effect of
                // running the process, not a change we can prevent; allow exactly those
                // two derived paths and nothing else (targetSeen/checkSeen stay required).
                string crashSav = Path.Combine(Path.GetDirectoryName(target),
                    Path.GetFileNameWithoutExtension(target) + "_Crash" + Path.GetExtension(target));
                string crashCheck = Path.Combine(Path.GetDirectoryName(target),
                    Path.GetFileNameWithoutExtension(target) + "_Crash" + Path.GetExtension(checkPath));
                List<string> unexpected = new List<string>();
                bool targetSeen = false;
                bool checkSeen = false;
                foreach (string c in changes)
                {
                    // change lines use the "<VERB>: <path>" form; ':' cannot occur in
                    // Windows file names so the first ": " cleanly separates
                    string path = c.Substring(c.IndexOf(": ") + 2).Trim();
                    if (SameFullPath(path, crashSav) || SameFullPath(path, crashCheck))
                    {
                        Log.Write("CHANGE (engine crash save, allowed): " + path);
                        continue;
                    }
                    Log.Write("CHANGE: " + c);
                    if (SameFullPath(path, target)) { targetSeen = true; continue; }
                    if (SameFullPath(path, checkPath)) { checkSeen = true; continue; }
                    if (SameFullPath(path, zxLogFull)) continue;
                    unexpected.Add(c);
                }
                if (unexpected.Count > 0)
                {
                    foreach (string u in unexpected) Log.Write("UNEXPECTED FILE CHANGE: " + u);
                    throw new Day0GenException("Unexpected file changes detected (see log). Only " + target +
                                               " / " + checkPath + " (+engine ZXLog.txt " + zxLogFull +
                                               ") may change.");
                }
                // H2: a successful full run MUST show both new artifacts in the diff. An
                // empty diff (or a missing artifact) means the snapshot verification itself
                // failed - that is an abort, not a log line.
                if (changes.Count == 0)
                    throw new Day0GenException("No file changes detected after the full run - snapshot verification " +
                                               "failed (fail closed).");
                if (!targetSeen || !checkSeen)
                    throw new Day0GenException("Snapshot verification failed: the diff does not contain both new " +
                                               "artifacts (target seen=" + targetSeen + ", zxcheck seen=" + checkSeen +
                                               ") - fail closed.");

                Log.Write("PHASE full COMPLETE: " + target);
                Log.Write("=== Day0Gen OK ===");
                return 0;
            }
            finally
            {
                // Every exit path (sequence failure, snapshot abort, success) must
                // drop the handler: it fires on every first-chance exception
                // process-wide.
                UnregisterFirstChanceHandler();
            }
        }

        // ---------------------------------------------------------------------
        // Construction + generation + save + verification, extracted from RunFull
        // so the whole sequence can be dispatched (main thread by default, legacy
        // engine-UI marshal under --ui-marshal; see DispatchSequence). Reads the
        // static opts / refl / managerInstance fields; the before/after snapshot
        // context lives in RunFull and is not needed here.
        // ---------------------------------------------------------------------
        private static void RunConstructGenerateSave(string target, string checkPath, string effectiveSavesDir)
        {
            LogProjectDiagnostics();

            // Bracket the engine-side ZXLog output around the generation call so the
            // interleaving with the engine's own scene-change logs is visible.
            DumpZxLogTail(effectiveSavesDir, 15);
            // H5: re-verify the theme table immediately before the generator invoke -
            // seconds of engine scene activity passed since the rebuild; this closes
            // the re-poisoning window to microseconds. Aborts on any poisoned shape.
            ReVerifyThemeTableQuick();
            // Gate on the engine's entity-default-params dict: it is filled by a
            // PostMethods_OnStartFrame action that runs after FromID becomes non-null,
            // so a generator invoke here would otherwise race it (KeyNotFoundException
            // in CreateInstance). See WaitForEntityDefaultParamsGate.
            WaitForEntityDefaultParamsGate();
            // The engine's startup fade-to-start-screen transition completes
            // asynchronously and its onFinish wipes the level; wait for it to settle
            // before generating/SetLevel so no pending scene change can tear the level
            // down before the save. See WaitForStartScreenSettled.
            WaitForStartScreenSettled(effectiveSavesDir);

            // STRATEGY CHANGE (notes/code-review-3.md C1/E + notes/loading-dialog-contract.md):
            // the engine's manager loading dialog owns the correct start-game lifecycle,
            // but its dispatch is gated on an overlay fade animation (create runs only on
            // OnFinished) and it drops the delegate when DXGame.Scene == null. The live run
            // stalled after ChangeScene and create never started. InvokeStartGameEnvelope
            // replicates the dialog's non-animation envelope DIRECTLY (pause, dispose/nul
            // the live menu game system, set IsLoading, run create on a Task) and runs the
            // save on that same Task right after create returns - no frame-queue dependency,
            // since the engine is paused and the start-frame queue is not draining. The
            // gates above remain as readiness proof.
            dialogTarget = target;
            dialogCheckPath = checkPath;
            dialogSavesDir = effectiveSavesDir;
            dialogGs = null;
            dialogParams = null;
            dialogLs = null;
            dialogSys = null;
            dialogLevel = null;
            dialogCreateException = null;
            dialogSaveException = null;
            dialogCreateDone = new ManualResetEvent(false);
            dialogSaveDone = new ManualResetEvent(false);

            Action createAction = new Action(CreateGameStateAndLevel);
            Action saveAfterAction = new Action(SaveGeneratedState);
            InvokeStartGameEnvelope(createAction, saveAfterAction);

            Log.Write("DIALOG: waiting up to " + (DialogCompletionTimeoutMs / 1000) +
                      "s for the create+save Task to signal ...");
            if (!dialogSaveDone.WaitOne(DialogCompletionTimeoutMs))
            {
                bool createFinished = dialogCreateDone.WaitOne(0);
                if (dialogCreateException != null)
                    throw new Day0GenException("Manager loading dialog: create delegate failed, so saveAfter " +
                        "never ran (fail closed). See DIALOG CREATE in the log.", dialogCreateException);
                throw new Day0GenException("Manager loading dialog: saveAfter did not complete within " +
                    (DialogCompletionTimeoutMs / 1000) + "s (create finished=" + createFinished +
                    ") - the create+save Task likely threw or stalled (fail closed). See ZXLog.");
            }
            if (dialogCreateException != null)
                throw new Day0GenException("Manager loading dialog: create delegate failed (fail closed).",
                    dialogCreateException);
            if (dialogSaveException != null)
            {
                // C4: the saveAfter existence checks may have caught a partial write;
                // never leave a bad artifact behind (RunFull refuses to overwrite).
                CleanupWrittenArtifacts(target, checkPath);
                throw new Day0GenException("Manager loading dialog: saveAfter delegate failed (fail closed).",
                    dialogSaveException);
            }
            Log.Write("DIALOG: create and saveAfter both completed.");

            // ---- post-save verification (main thread) ------------------------------
            // zxcheck / read-back / CC assertion / save-list. Any failure here means a
            // structurally valid but wrong save may be on disk: C4 deletes it before
            // throwing so a bad save cannot wedge subsequent runs.
            try
            {
                string sig = (string)refl.Invoke("signing(target,2)", refl.SigningMethod, null, target, 2);
                string written = File.ReadAllText(checkPath).Trim();
                if (sig != written)
                    throw new Day0GenException("zxcheck mismatch: file contains '" + written + "', signer produced '" + sig + "'.");
                Log.Write("zxcheck verified: " + sig);

                object readBack = ReadGameStateFromSave(target);
                if (readBack == null)
                    throw new Day0GenException("Read-back of saved state returned null.");
                object readName = refl.GetProp("read-back.Name", refl.GameStateType.GetProperty("Name"), readBack);
                if (string.Compare((string)readName, opts.Name, StringComparison.Ordinal) != 0)
                    throw new Day0GenException("Read-back name '" + readName + "' != '" + opts.Name + "'.");
                Log.Write("Read-back OK: ZXGameState named '" + readName + "'.");

                // Fail-closed: the save is structurally valid but PreSave rebuilds
                // LevelEntities from the LIVE registry; a missing Command Center there is
                // silently dropped at write time. Prove it in the saved bytes.
                AssertReadBackHasCommandCenter(readBack);

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
            catch
            {
                // C4: only the post-save verification failure path cleans up; the
                // success path above never deletes.
                CleanupWrittenArtifacts(target, checkPath);
                throw;
            }
        }

        // ---------------------------------------------------------------------
        // C1 strategy (notes/code-review-3.md + notes/loading-dialog-contract.md):
        // the manager loading dialog runs CreateGameStateAndLevel on a Task thread.
        // It is the real survival/CC handler's construct -> generate -> SetLevel
        // sequence (minus the challenge lines) plus the tool's SetLevel continuation
        // (adopt). It must NOT save - the envelope runs SaveGeneratedState directly on
        // this same Task right after it returns. A throw here is caught by the envelope,
        // which then never runs saveAfter, so the run fails closed (the main thread
        // times out or sees the stored exception).
        // ---------------------------------------------------------------------
        private static void CreateGameStateAndLevel()
        {
            try
            {
                // ---- construction: mirror CC handler minus challenge lines ------------
                // The dialog has already paused the game, disposed the live menu game
                // system and set IsLoading; this is the create delegate body.
                // save mode: derive the six generation params from the source save HERE
                // and not during engine startup. Reading a foreign ZXGameState while the
                // start screen is still loading corrupts the scene machine and the
                // loading-screen render throws NRE (DXProjectImage.get_ImageArea); the
                // envelope has already settled and paused the engine by this point.
                // Explicit CLI flags win over the save-derived values.
                if (opts.Command == "save")
                    ApplySaveParams();
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
                object themeValue = opts.Theme == null
                    ? Enum.Parse(refl.MapThemeEnum, "None")
                    : ParseEnumOrAbort(refl.MapThemeEnum, opts.Theme, "--theme");
                refl.SetProp("params.ThemeType=" + themeValue, refl.ParamsType.GetProperty("ThemeType"), p, themeValue);
                if (opts.Difficulty != null)
                    refl.SetProp("params.DifficultyType=" + opts.Difficulty,
                                 refl.ParamsType.GetProperty("DifficultyType"), p,
                                 ParseEnumOrAbort(refl.DifficultyEnum, opts.Difficulty, "--difficulty"));
                // DifficultyType otherwise intentionally left at default (None) - CC handler parity.
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

                Log.Write("Generating level (engine logs 'Random Map Creation with seed: " + opts.Seed + "') ...");
                object level = refl.Invoke("generator(params)", refl.GenerateMethod, null, p);
                if (level == null)
                    throw new Day0GenException("Generator returned null level.");

                // Record the ZXLog byte offset BEFORE SetLevel: its engine-side work
                // (scene objects, minimap, ChangeScene) logs asynchronously, and the
                // completion poll below must only look at bytes written after this point.
                long zxLogOffset = ZxLogLengthBeforeSetLevel(dialogSavesDir);

                bool setLevelSwallowed = false;
                try
                {
                    refl.Invoke("gamesystem.SetLevel(level)", refl.SetLevelMethod, sys, level);
                }
                catch (Day0GenException e)
                {
                    Exception innermost = e;
                    while (innermost.InnerException != null) innermost = innermost.InnerException;
                    if (!(innermost is NullReferenceException))
                        throw;
                    // SetLevel has already assigned the current-level field and adopted
                    // the level when per-entity scene-object creation NREs out (the NRE
                    // escapes at the DXLevel.cs:438 catch handling). Scene objects are
                    // render-layer only - NOT serialized into saves (rebuilt from
                    // LevelEntities on load) - so this failure is expected to be
                    // recoverable. The two verifications below prove engine-side
                    // completion before any save byte is written; a wrong guess aborts.
                    setLevelSwallowed = true;
                    Log.Write("SetLevel threw (expected: recoverable scene-object failure); verifying engine-side completion...");
                    LogExceptionChain("SETLEVEL swallowed", e);
                }

                // Post-SetLevel verification - required on BOTH the swallowed and the
                // clean path; only after both pass may the save proceed.
                VerifySetLevelEngineCompletion(dialogSavesDir, zxLogOffset);
                VerifyCurrentLevelField(sys, level);
                if (setLevelSwallowed)
                    Log.Write("SETLEVEL VERIFY: PASSED after the swallowed scene-object NRE - " +
                              "level fully adopted engine-side; proceeding to the save.");

                // ---- adopt level into ZXLevelState (SetLevel continuation) --------------
                // The 17:39 run proved the scene-object NRE escapes BEFORE SetLevel's
                // new-level branch reaches the ZXLevelState adoption - the state PreSave
                // dereferences (CurrentGeneratedLevel) was never built. Complete it here.
                AdoptLevelIntoLevelState(ls, sys, level);

                DumpZxLogTail(dialogSavesDir, 15);

                dialogGs = gs;
                dialogParams = p;
                dialogLs = ls;
                dialogSys = sys;
                dialogLevel = level;
                Log.Write("CREATE: construct/generate/SetLevel/adopt complete; the dialog will now hand " +
                          "saveAfter to the engine finish frame.");
            }
            catch (Exception ex)
            {
                dialogCreateException = ex;
                Log.Write("CREATE: delegate FAILED - rethrowing so the dialog skips saveAfter (fail closed).");
                LogExceptionChain("DIALOG CREATE", ex);
                throw;
            }
            finally
            {
                if (dialogCreateDone != null) dialogCreateDone.Set();
            }
        }

        // ---------------------------------------------------------------------
        // C1 strategy: the envelope's saveAfter delegate, run directly on the create
        // Task right after CreateGameStateAndLevel returns (the engine is paused, so
        // there is no frame queue to depend on). The save path is:
        //   re-assert state -> snapshot the generated level's Entities -> invoke
        //   ZXLevelState.PreSave explicitly -> overwrite LevelEntities /
        //   LevelFastSerializedEntities with the snapshot -> native writer.
        //
        // WHY not the SaveState wrapper: its PreSave rebuilds LevelEntities from the
        // LIVE DXGame.ComponentsOfType<CSalvable>() registry (ZXLevelState.cs:1644-1672).
        // That registry is populated only by scene registration (CreateSceneObject /
        // AddToScene), and the tolerated scene-object NRE aborts the engine's
        // registration step, so the rebuild yields nothing and the wrapper writes an
        // EMPTY save (live-proven: read-back LevelEntities count=0). The native writer
        // serializes ZXGameState.Current directly, so a hand-built LevelEntities from
        // the generated level's own entity list is what gets written.
        //
        // Exceptions are stored for the main thread (which owns the abort + C4 cleanup);
        // it never throws at its caller.
        // ---------------------------------------------------------------------
        private static void SaveGeneratedState()
        {
            try
            {
                object gs = dialogGs;
                object ls = dialogLs;
                object sys = dialogSys;
                object level = dialogLevel;

                ReAssertStateBeforeSave(gs, ls, sys);

                // Diagnostic-only (never aborts): locate the Command Center before PreSave.
                LogCommandCenterDiagnostics(level);

                // 1) SNAPSHOT before PreSave clears the generated level's entity list.
                PreSaveEntitySnapshot snapshot = BuildPreSaveEntitySnapshot(level);

                // 2) PreSave: clears CurrentGeneratedLevel.Entities + CSalvable
                //    ExtraEntities, rebuilds the dictionaries from the (empty) live
                //    registry, and sets the camera areas / fog.
                if (refl.PreSaveMethod == null)
                    throw new Day0GenException("ZXLevelState.PreSave (#=zQXHqcVh9mGZZ) not found - " +
                        "cannot prepare the save state; aborting before the save (fail closed).");
                if (ls == null)
                    throw new Day0GenException("dialogLs is null - cannot invoke PreSave");
                refl.Invoke("ZXLevelState.PreSave() [clear + rebuild]", refl.PreSaveMethod, ls);

                // 3) OVERWRITE with the snapshot; the rebuilt live-registry dicts are empty.
                PropertyInfo levelEntitiesProp = FindPropertyUp(refl.LevelStateType, "LevelEntities");
                if (levelEntitiesProp == null)
                    throw new Day0GenException("Save: ZXLevelState.LevelEntities property not found (build drift?)");
                PropertyInfo fastProp = FindPropertyUp(refl.LevelStateType, "LevelFastSerializedEntities");
                if (fastProp == null)
                    throw new Day0GenException("Save: ZXLevelState.LevelFastSerializedEntities property not found (build drift?)");
                refl.SetProp("ls.LevelEntities = snapshot (overwrite PreSave's empty rebuild)",
                    levelEntitiesProp, ls, snapshot.LevelEntities);
                refl.SetProp("ls.LevelFastSerializedEntities = snapshot (overwrite PreSave's empty rebuild)",
                    fastProp, ls, snapshot.FastSerializedEntities);

                // 4) Native writer: serializes ZXGameState.Current (re-asserted above), so
                //    the overwritten dictionaries are what gets written. It throws on
                //    failure (unlike the old wrapper), so the existence checks below are a
                //    belt-and-braces check, not the only failure signal.
                if (refl.SaveWriterMethod == null)
                    throw new Day0GenException("native save writer (#=zMtGuEM2lBSlZ5BGWvg==) not found - " +
                        "the primary save path cannot run; aborting (fail closed).");
                if (managerInstance == null)
                    throw new Day0GenException("managerInstance is null - cannot invoke the native save writer");
                Log.Write("Saving via the native writer (serializes ZXGameState.Current directly;" +
                          " name='" + opts.Name + "', target=" + dialogTarget +
                          ", zxcheck=" + dialogCheckPath + ").");
                refl.Invoke("manager native save writer (path)->void", refl.SaveWriterMethod,
                            managerInstance, dialogTarget);

                if (!File.Exists(dialogTarget))
                    throw new Day0GenException("Save did not produce " + dialogTarget +
                        " (the native writer throws on failure - check ZXLog)");
                if (!File.Exists(dialogCheckPath))
                    throw new Day0GenException("Save did not produce " + dialogCheckPath +
                        " (the native writer throws on failure - check ZXLog)");
                Log.Write("SAVEAFTER: save artifacts exist (" + dialogTarget + " + " + dialogCheckPath + ").");
            }
            catch (Exception ex)
            {
                dialogSaveException = ex;
                Log.Write("SAVEAFTER: delegate FAILED - the main thread will abort and clean up (fail closed).");
                LogExceptionChain("DIALOG SAVEAFTER", ex);
            }
            finally
            {
                if (dialogSaveDone != null) dialogSaveDone.Set();
            }
        }

        // Snapshot of the generated level's serializable entities, built before PreSave
        // clears the level's own entity list. Plain fields (rather than out params) so
        // the C#5 audit's inline-out heuristic is not tripped.
        private sealed class PreSaveEntitySnapshot
        {
            public object LevelEntities;
            public object FastSerializedEntities;
        }

        // Builds the two entity dictionaries PreSave would normally rebuild from the
        // live registry, but from the generated level's OWN Entities list (snapshotted
        // before PreSave clears it). The predicate replicates ZXLevelState.cs:1644-1672:
        //   LevelEntities: keep when NOT fast-serializing; for a ZXEntity whose life
        //     IsAlive == false keep only when it is a Structure with IsBeingBuilt == true.
        //   LevelFastSerializedEntities: fast-serializing ZXEntities whose life is not
        //     dead, grouped by entity.IDTemplate.Value as (ID, Position) pairs.
        // A missing filter member logs and fails open (not fast / keep); an unbuildable
        // DXTupla2<,> omits the fast entities (logged) but never aborts. ID / IDTemplate /
        // Position are required and abort on a miss (the save would be corrupt otherwise).
        private static PreSaveEntitySnapshot BuildPreSaveEntitySnapshot(object level)
        {
            PropertyInfo entitiesProp = FindPropertyUp(refl.LevelStateType, "LevelEntities");
            if (entitiesProp == null)
                throw new Day0GenException("Save snapshot: ZXLevelState.LevelEntities property not found (build drift?)");
            PropertyInfo fastProp = FindPropertyUp(refl.LevelStateType, "LevelFastSerializedEntities");
            if (fastProp == null)
                throw new Day0GenException("Save snapshot: ZXLevelState.LevelFastSerializedEntities property not found (build drift?)");

            object entitiesRaw = ReadMemberValue(level, "Entities");
            System.Collections.IEnumerable entities = entitiesRaw as System.Collections.IEnumerable;
            if (entities == null)
                throw new Day0GenException("Save snapshot: generated level Entities is not IEnumerable (" +
                                           DescribeValue(entitiesRaw) + ")");

            object levelDictObj = Activator.CreateInstance(entitiesProp.PropertyType);
            System.Collections.IDictionary levelById = levelDictObj as System.Collections.IDictionary;
            if (levelById == null)
                throw new Day0GenException("Save snapshot: LevelEntities type is not IDictionary (" +
                                           entitiesProp.PropertyType.FullName + ")");
            object fastDictObj = Activator.CreateInstance(fastProp.PropertyType);
            System.Collections.IDictionary fastByTemplate = fastDictObj as System.Collections.IDictionary;
            if (fastByTemplate == null)
                throw new Day0GenException("Save snapshot: LevelFastSerializedEntities type is not IDictionary (" +
                                           fastProp.PropertyType.FullName + ")");

            // Predicate members, resolved once. A miss logs and fails open.
            Type zxEntityType = FindTypeAnyOrder("ZX.Entities.ZXEntity", refl.TabAssembly);
            PropertyInfo useFastProp = zxEntityType != null ? FindPropertyUp(zxEntityType, "UseFastSerializing") : null;
            Type structureType = FindTypeAnyOrder("ZX.Entities.Structure", refl.TabAssembly);
            PropertyInfo beingBuiltProp = structureType != null ? FindPropertyUp(structureType, "IsBeingBuilt") : null;
            string lifeName = GameReflector.Unescape("_0023_003DzS6TvFuwF_68l");
            MethodInfo lifeMethod = zxEntityType != null ? FindMethodUp(zxEntityType, lifeName, 0) : null;
            if (zxEntityType == null)
                Log.Write("SAVE SNAPSHOT: ZX.Entities.ZXEntity not found - treating every entity as not fast-serializing.");
            if (useFastProp == null)
                Log.Write("SAVE SNAPSHOT: ZXEntity.UseFastSerializing not found - treating every entity as not fast-serializing.");
            if (lifeMethod == null)
                Log.Write("SAVE SNAPSHOT: ZXEntity life accessor (" + lifeName + ") not found - treating every entity as alive.");
            if (beingBuiltProp == null)
                Log.Write("SAVE SNAPSHOT: Structure.IsBeingBuilt not found - dead entities are kept (filter fails open).");

            // DXTupla2<ulong, PointF> construction (fast-serialized entities only).
            Type tupleDef = FindTypeAnyOrder("DXVision.DXTupla2`2", refl.DxAssembly);
            if (tupleDef == null)
                tupleDef = FindGenericTypeBySimpleName(refl.DxAssembly, "DXTupla2", 2);
            Type tupleType = null;
            ConstructorInfo tupleCtor = null;
            FieldInfo tupleAField = null, tupleBField = null;
            PropertyInfo tupleAProp = null, tupleBProp = null;
            if (tupleDef != null)
            {
                try { tupleType = tupleDef.MakeGenericType(typeof(ulong), typeof(System.Drawing.PointF)); }
                catch (Exception ex)
                {
                    Log.Write("SAVE SNAPSHOT: DXTupla2<,> MakeGenericType(ulong,PointF) failed: " + DescribeException(ex));
                    tupleType = null;
                }
            }
            if (tupleType != null)
            {
                foreach (ConstructorInfo c in tupleType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (c.GetParameters().Length == 2) { tupleCtor = c; break; }
                }
                if (tupleCtor == null)
                {
                    tupleAField = FindFieldUp(tupleType, "A");
                    tupleBField = FindFieldUp(tupleType, "B");
                    if (tupleAField == null) tupleAProp = FindPropertyUp(tupleType, "A");
                    if (tupleBField == null) tupleBProp = FindPropertyUp(tupleType, "B");
                }
            }
            bool canTuple = tupleType != null &&
                (tupleCtor != null || ((tupleAField != null || tupleAProp != null) &&
                                       (tupleBField != null || tupleBProp != null)));
            if (!canTuple)
                Log.Write("SAVE SNAPSHOT: DXTupla2<ulong,PointF> unavailable (type=" +
                          (tupleDef == null ? "not found" : tupleDef.FullName) +
                          ") - fast-serialized entities will be OMITTED from the save.");

            // CSalvable filter. PreSave sources LevelEntities from the LIVE
            // DXGame.Current.ComponentsOfType<CSalvable>() registry (ZXLevelState.cs:1644-1672),
            // i.e. only entities carrying a CSalvable component. SetLevel's
            // AddComponent<CSalvable>() loop runs BEFORE UpdateLevel moves the generated
            // terrain/extra entities into level.Entities, so those entities never carry
            // CSalvable; the game regenerates the terrain from the map layers on load.
            // Writing them once AND letting the game regenerate them double-creates terrain
            // and crashes (ZX.Cliff.OnSceneAdded NRE). Fail closed on a discovery miss -
            // falling back to "include everything" is exactly that double-create bug.
            Type csalvableType = FindTypeAnyOrder("ZX.Components.CSalvable", refl.TabAssembly);
            if (csalvableType == null)
                throw new Day0GenException("Save snapshot: ZX.Components.CSalvable type not found (build drift?) - " +
                    "cannot filter level.Entities to the CSalvable registry; aborting (fail closed).");
            Type dxEntityType = FindTypeAnyOrder("DXVision.DXEntity", refl.DxAssembly);
            MethodInfo hasComponentDef = FindGenericBoolMethodUp(dxEntityType, "HasComponent", 0);
            if (hasComponentDef == null)
                throw new Day0GenException("Save snapshot: DXEntity.HasComponent<T>() not found (build drift?) - " +
                    "cannot filter level.Entities to the CSalvable registry; aborting (fail closed).");
            MethodInfo hasComponent;
            try { hasComponent = hasComponentDef.MakeGenericMethod(csalvableType); }
            catch (Exception ex)
            {
                throw new Day0GenException("Save snapshot: HasComponent<" + csalvableType.FullName +
                    "> MakeGenericMethod failed: " + DescribeException(ex));
            }

            Type fastListType = fastProp.PropertyType.GetGenericArguments()[1]; // List<DXTupla2<...>>
            int fastCount = 0, fastOmitted = 0, deadDropped = 0;
            int totalEntities = 0, csalvableKept = 0, csalvableSkipped = 0;
            foreach (object entity in entities)
            {
                totalEntities++;
                if (entity == null) { csalvableSkipped++; continue; }
                object hasObj;
                try { hasObj = hasComponent.Invoke(entity, null); }
                catch (Exception ex)
                {
                    throw new Day0GenException("Save snapshot: HasComponent<CSalvable>() invoke failed on " +
                        (entity.GetType().FullName != null ? entity.GetType().FullName : entity.GetType().Name) +
                        ": " + DescribeException(ex));
                }
                if (!(hasObj is bool) || !(bool)hasObj) { csalvableSkipped++; continue; }
                csalvableKept++;
                bool isZx = zxEntityType != null && zxEntityType.IsInstanceOfType(entity);
                bool useFast = false;
                if (isZx && useFastProp != null)
                {
                    object uf = useFastProp.GetValue(entity, null);
                    useFast = uf is bool && (bool)uf;
                }

                bool alive = true;
                if (isZx && lifeMethod != null)
                {
                    object life = lifeMethod.Invoke(entity, null);
                    if (life != null) alive = ReadBoolMember(life, "IsAlive", true);
                }

                if (useFast)
                {
                    if (!alive) { deadDropped++; continue; }
                    fastCount++;
                    if (!canTuple) { fastOmitted++; continue; }
                    ulong fastId = Convert.ToUInt64(ReadMemberValue(entity, "ID"), CultureInfo.InvariantCulture);
                    object pos = ReadMemberValue(entity, "Position");
                    object idTemplate = ReadMemberValue(entity, "IDTemplate");
                    if (idTemplate == null)
                        throw new Day0GenException("Save snapshot: fast entity IDTemplate is null (ID=" + fastId + ")");
                    // Nullable<T> boxes as its unwrapped value, so a non-null integral IDTemplate
                    // exposes no "Value" member; only a template-id wrapper needs the member read.
                    ulong templateKey;
                    if (idTemplate is ulong || idTemplate is uint || idTemplate is int || idTemplate is long ||
                        idTemplate is ushort || idTemplate is short || idTemplate is byte || idTemplate is sbyte)
                        templateKey = Convert.ToUInt64(idTemplate, CultureInfo.InvariantCulture);
                    else
                        templateKey = Convert.ToUInt64(ReadMemberValue(idTemplate, "Value"), CultureInfo.InvariantCulture);
                    object tuple = MakeFastTuple(fastId, pos, tupleCtor, tupleType,
                                                 tupleAField, tupleBField, tupleAProp, tupleBProp);
                    if (tuple == null) { fastOmitted++; continue; }
                    object list = fastByTemplate.Contains(templateKey) ? fastByTemplate[templateKey] : null;
                    if (list == null)
                    {
                        list = Activator.CreateInstance(fastListType);
                        fastByTemplate[templateKey] = list;
                    }
                    ((System.Collections.IList)list).Add(tuple);
                }
                else
                {
                    bool keep = true;
                    if (!alive)
                    {
                        // PreSave keeps a dead ZXEntity only when it is a Structure being
                        // built; a discovery miss keeps it (fail open, logged above).
                        if (structureType == null || beingBuiltProp == null) keep = true;
                        else if (structureType.IsInstanceOfType(entity)) keep = ReadBoolMember(entity, "IsBeingBuilt", false);
                        else keep = false;
                    }
                    if (!keep) { deadDropped++; continue; }
                    ulong id = Convert.ToUInt64(ReadMemberValue(entity, "ID"), CultureInfo.InvariantCulture);
                    levelById[id] = entity;
                }
            }

            bool cc = false;
            foreach (object v in levelById.Values)
            {
                if (IsCommandCenter(v)) { cc = true; break; }
            }

            PreSaveEntitySnapshot snapshot = new PreSaveEntitySnapshot();
            snapshot.LevelEntities = levelDictObj;
            snapshot.FastSerializedEntities = fastDictObj;
            Log.Write("SAVE SNAPSHOT: level.Entities=" + totalEntities + ", CSalvable kept=" + csalvableKept +
                      ", skipped=" + csalvableSkipped + ".");
            Log.Write("SAVE SNAPSHOT: LevelEntities=" + levelById.Count + " (CommandCenter=" + cc +
                      ", dead/unbuilt dropped=" + deadDropped + "), fast-serialized entities=" + fastCount +
                      " in " + fastByTemplate.Count + " template group(s)" +
                      (fastOmitted > 0 ? ", OMITTED=" + fastOmitted : "") + ".");
            return snapshot;
        }

        // Constructs one DXTupla2<ulong, PointF> via the 2-arg ctor, or (fallback) the
        // default ctor + A/B fields/properties. Returns null when neither shape exists,
        // which the caller counts as an omission (never an abort).
        private static object MakeFastTuple(ulong id, object pos, ConstructorInfo ctor, Type tupleType,
            FieldInfo aField, FieldInfo bField, PropertyInfo aProp, PropertyInfo bProp)
        {
            if (ctor != null)
            {
                ParameterInfo[] ps = ctor.GetParameters();
                object a = ConvertArg(id, ps[0].ParameterType);
                object b = ConvertArg(pos, ps[1].ParameterType);
                return ctor.Invoke(new object[] { a, b });
            }
            if (tupleType == null) return null;
            object t = Activator.CreateInstance(tupleType);
            if (aField != null) aField.SetValue(t, ConvertArg(id, aField.FieldType));
            else if (aProp != null && aProp.GetSetMethod(true) != null) aProp.SetValue(t, ConvertArg(id, aProp.PropertyType), null);
            else return null;
            if (bField != null) bField.SetValue(t, ConvertArg(pos, bField.FieldType));
            else if (bProp != null && bProp.GetSetMethod(true) != null) bProp.SetValue(t, ConvertArg(pos, bProp.PropertyType), null);
            else return null;
            return t;
        }

        // Reads a bool property (base chain) then field; returns the fallback on a miss.
        private static bool ReadBoolMember(object obj, string name, bool fallback)
        {
            PropertyInfo p = FindPropertyUp(obj.GetType(), name);
            if (p != null && p.PropertyType == typeof(bool)) return (bool)p.GetValue(obj, null);
            FieldInfo f = FindFieldUp(obj.GetType(), name);
            if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(obj);
            return fallback;
        }

        // Finds an instance/static method by real metadata name + arg count, walking the
        // base chain (DeclaredOnly per level so an override wins over a base declaration).
        private static MethodInfo FindMethodUp(Type t, string name, int argCount)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                MethodInfo[] ms = null;
                try
                {
                    ms = cur.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (ms == null) continue;
                foreach (MethodInfo m in ms)
                {
                    if (m.Name == name && m.GetParameters().Length == argCount) return m;
                }
            }
            return null;
        }

        // Finds a generic, bool-returning method definition by name + arg count,
        // walking the base chain (DeclaredOnly per level). Used to resolve
        // DXEntity.HasComponent<T>() for the CSalvable snapshot filter.
        private static MethodInfo FindGenericBoolMethodUp(Type t, string name, int argCount)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                MethodInfo[] ms = null;
                try
                {
                    ms = cur.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (ms == null) continue;
                foreach (MethodInfo m in ms)
                {
                    if (m.Name != name) continue;
                    if (!m.IsGenericMethodDefinition) continue;
                    if (m.GetParameters().Length != argCount) continue;
                    if (m.ReturnType != typeof(bool)) continue;
                    return m;
                }
            }
            return null;
        }

        // Fallback generic-type resolution (used only when the exact DXVision.DXTupla2`2
        // name misses): scan the assembly for a generic definition with the arity and the
        // simple-name prefix.
        private static Type FindGenericTypeBySimpleName(Assembly asm, string simpleName, int typeArgCount)
        {
            Type[] types = GameReflector.SafeGetTypes(asm);
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t == null || !t.IsGenericTypeDefinition) continue;
                if (t.Name != null && t.Name.StartsWith(simpleName, StringComparison.Ordinal) &&
                    t.GetGenericArguments().Length == typeArgCount)
                    return t;
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // Start-game envelope: replicate the manager loading dialog's non-animation
        // lifecycle directly (notes/loading-dialog-contract.md). The dialog's dispatch
        // is gated on an overlay fade animation and drops the delegate when
        // DXGame.Scene == null, so relying on it stalled the live run after ChangeScene
        // (create never started). This runs the same events with no animation/scene
        // dependency: pause the engine, dispose+null the live game system, set IsLoading,
        // run create on a Task (so SetLevel's InvokeOnStartFrame+WaitOne still signals),
        // then run saveAfter directly on that same Task right after create returns. The
        // engine is paused by the envelope, so the frame queue is not draining and the
        // dialog's engine start-frame saveAfter handoff (InvokeOnStartFrame) never ran in
        // the live run; a direct call needs no frame pump. The UI mutation runs on the
        // engine UI thread (RunOnEngineUiThread) because the dialog itself did; nothing
        // invokes the dialog anymore.
        // ---------------------------------------------------------------------
        private static void InvokeStartGameEnvelope(Action createAction, Action saveAfterAction)
        {
            // Discovery. DXVision is embedded/undecrypted, so every shape is resolved at
            // runtime via stable engine names; a required miss aborts (never runs create
            // inline - that reintroduces the out-of-band crash review #3 identified).
            Type dxGameType = FindTypeAnyOrder("DXVision.DXGame", refl.DxAssembly);
            if (dxGameType == null)
                throw new Day0GenException("Start-game envelope: DXVision.DXGame type not discovered - " +
                    "cannot pause/dispose (aborting; no inline fallback). See notes/loading-dialog-contract.md.");

            PropertyInfo dxCurrentProp = FindPropertyUp(dxGameType, "Current");
            if (dxCurrentProp != null)
            {
                MethodInfo currentGetter = dxCurrentProp.GetGetMethod(true);
                if (currentGetter == null || !currentGetter.IsStatic) dxCurrentProp = null;
            }
            FieldInfo dxCurrentField = (dxCurrentProp == null) ? FindFieldUp(dxGameType, "Current") : null;
            if (dxCurrentField != null && !dxCurrentField.IsStatic) dxCurrentField = null;
            if (dxCurrentProp == null && dxCurrentField == null)
                throw new Day0GenException("Start-game envelope: DXVision.DXGame.Current (static property/field) " +
                    "not discovered - aborting (no inline fallback).");

            PropertyInfo pausedProp = FindPropertyUp(dxGameType, "Paused");
            if (pausedProp != null && (pausedProp.PropertyType != typeof(bool) || pausedProp.GetSetMethod(true) == null))
                pausedProp = null;
            FieldInfo pausedField = FindFieldUp(dxGameType, "Paused");
            if (pausedField != null && (pausedField.FieldType != typeof(bool) || pausedField.IsInitOnly))
                pausedField = null;
            if (pausedProp == null && pausedField == null)
                throw new Day0GenException("Start-game envelope: DXVision.DXGame.Paused (bool, writable) not " +
                    "discovered - aborting (no inline fallback).");

            if (refl.CurrentGameSystemProp == null)
                throw new Day0GenException("Start-game envelope: manager.CurrentGameSystem property not " +
                    "discovered - aborting (no inline fallback).");
            if (managerInstance == null)
                throw new Day0GenException("Start-game envelope: managerInstance is null - cannot pause/dispose " +
                    "(aborting; no inline fallback).");

            // IsLoading is optional (the dialog sets it, but the envelope works without it).
            PropertyInfo isLoadingProp = FindPropertyUp(refl.ManagerType, "IsLoading");
            if (isLoadingProp != null && (isLoadingProp.PropertyType != typeof(bool) ||
                isLoadingProp.GetSetMethod(true) == null))
                isLoadingProp = null;
            FieldInfo isLoadingField = FindFieldUp(refl.ManagerType, "IsLoading");
            if (isLoadingField != null && (isLoadingField.FieldType != typeof(bool) || isLoadingField.IsInitOnly))
                isLoadingField = null;
            if (isLoadingProp == null && isLoadingField == null)
                Log.Write("ENVELOPE: manager.IsLoading not found - skipped (optional).");

            Log.Write("ENVELOPE: discovered DXGame.Current, DXGame.Paused, " +
                      "manager.CurrentGameSystem, manager.IsLoading=" +
                      (isLoadingProp != null || isLoadingField != null));

            MethodInvoker runEnvelope = delegate()
            {
                object game = dxCurrentProp != null ? dxCurrentProp.GetValue(null, null) : dxCurrentField.GetValue(null);
                if (game == null)
                    throw new Day0GenException("Start-game envelope: DXVision.DXGame.Current is null - cannot " +
                        "pause/dispose (aborting; no inline fallback).");
                Log.Write("ENVELOPE: DXGame.Current = " + game.GetType().FullName + " - pausing the engine.");
                if (pausedProp != null) pausedProp.SetValue(game, true, null);
                else pausedField.SetValue(game, true);
                Log.Write("ENVELOPE: DXGame.Current.Paused = true.");

                object sys = refl.GetProp("manager.CurrentGameSystem (envelope)",
                    refl.CurrentGameSystemProp, managerInstance);
                if (sys != null)
                {
                    Log.Write("ENVELOPE: disposing previous game system " + sys.GetType().FullName + ".");
                    SetEnvelopeBool(sys, "Enabled", false);
                    InvokeSystemDispose(sys);
                    refl.SetProp("manager.CurrentGameSystem=null (envelope)",
                        refl.CurrentGameSystemProp, managerInstance, null);
                    Log.Write("ENVELOPE: previous game system disposed; manager.CurrentGameSystem = null.");
                }
                else
                {
                    Log.Write("ENVELOPE: manager.CurrentGameSystem already null - nothing to dispose.");
                }

                if (isLoadingProp != null) isLoadingProp.SetValue(managerInstance, true, null);
                else if (isLoadingField != null) isLoadingField.SetValue(managerInstance, true);
                if (isLoadingProp != null || isLoadingField != null)
                    Log.Write("ENVELOPE: manager.IsLoading = true.");

                Log.Write("ENVELOPE: starting create+save on a Task (engine paused; no frame-queue dependency).");
                System.Threading.Tasks.Task.Factory.StartNew(delegate()
                {
                    try
                    {
                        createAction();
                        saveAfterAction();
                    }
                    catch (Exception ex)
                    {
                        dialogCreateException = ex;
                        Log.Write("ENVELOPE: create/save FAILED - signaling the main thread (fail closed).");
                        LogExceptionChain("ENVELOPE CREATE", ex);
                        if (dialogSaveDone != null) dialogSaveDone.Set();
                    }
                });
            };
            RunOnEngineUiThread("start-game envelope",
                "if no save appears, the envelope's create+save Task stalled or threw before signaling; " +
                "check ZXLog.",
                runEnvelope);
        }

        // Writes a bool member (property or field) on an engine object. Enabled is not in
        // the envelope's required set, so a miss is logged and skipped.
        private static void SetEnvelopeBool(object target, string name, bool value)
        {
            PropertyInfo p = FindPropertyUp(target.GetType(), name);
            if (p != null && (p.PropertyType != typeof(bool) || p.GetSetMethod(true) == null)) p = null;
            FieldInfo f = FindFieldUp(target.GetType(), name);
            if (f != null && (f.FieldType != typeof(bool) || f.IsInitOnly)) f = null;
            if (p == null && f == null)
            {
                Log.Write("ENVELOPE: " + name + " not found on " + target.GetType().FullName + " - skipped.");
                return;
            }
            if (p != null) p.SetValue(target, value, null);
            else f.SetValue(target, value);
            Log.Write("ENVELOPE: " + name + " = " + value + ".");
        }

        // Calls the engine's instance Dispose() on the previous game system. Prefers the
        // reflected 0-arg method; falls back to IDisposable.
        private static void InvokeSystemDispose(object sys)
        {
            MethodInfo dispose = null;
            for (Type cur = sys.GetType(); cur != null && dispose == null; cur = cur.BaseType)
            {
                MethodInfo[] ms = null;
                try
                {
                    ms = cur.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (ms == null) continue;
                foreach (MethodInfo m in ms)
                {
                    if (m.Name == "Dispose" && m.GetParameters().Length == 0) { dispose = m; break; }
                }
            }
            if (dispose != null)
            {
                refl.Invoke("envelope: previous game system Dispose()", dispose, sys);
                return;
            }
            IDisposable disposable = sys as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
                Log.Write("ENVELOPE: previous game system disposed via IDisposable.");
                return;
            }
            Log.Write("ENVELOPE: previous game system exposes no Dispose() - skipped.");
        }

        // C4: a written-but-bad save must not survive. RunFull refuses to overwrite an
        // existing target, so leaving the file wedges every subsequent run until an
        // operator deletes it. Called ONLY on the post-save failure path.
        private static void CleanupWrittenArtifacts(string target, string checkPath)
        {
            string dir = Path.GetDirectoryName(target);
            string[] paths = new string[]
            {
                target,
                checkPath,
                Path.Combine(dir, Path.GetFileNameWithoutExtension(target) + "_Crash" + Path.GetExtension(target)),
                Path.Combine(dir, Path.GetFileNameWithoutExtension(checkPath) + "_Crash" + Path.GetExtension(checkPath))
            };
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        Log.Write("CLEANUP: deleted bad save artifact " + path);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("CLEANUP: failed to delete " + path + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // ---------------------------------------------------------------------
        // Post-SetLevel verification. SetLevel's per-entity scene-object creation
        // can throw a recoverable NullReferenceException (ZX.Components.CTerrainResource
        // fails ~3x, caught+logged by the engine; one NRE escapes at DXLevel.cs:438)
        // while the engine still COMPLETES level setup - ZXLog of the 17:26 run shows
        // GetMiniMapImage End -> "LoadLevel - Minimap OK" -> ChangeScene
        // pre-Invoke/Invoke -> Fade End after the throw. Both checks below run on the
        // swallowed-NRE and the clean path alike; either failing aborts before the
        // save is written.
        // ---------------------------------------------------------------------

        // Engine-side completion marker: logged by ZXSystem_GameLevel after the
        // minimap is built, i.e. after level setup finished.
        private const string SetLevelCompletionMarker = "LoadLevel - Minimap OK";

        // (a) poll the ZXLog portion appended after the pre-invoke offset for the
        // engine's own LoadLevel-completion marker (up to 90s, 1s interval).
        private static void VerifySetLevelEngineCompletion(string savesDir, long zxLogOffset)
        {
            Log.Write("SETLEVEL VERIFY: polling ZXLog (up to 90s, 1s interval) for '" +
                      SetLevelCompletionMarker + "' ...");
            DateTime deadline = DateTime.UtcNow.AddSeconds(90);
            int polls = 0;
            while (DateTime.UtcNow < deadline)
            {
                polls++;
                string portion = ReadZxLogPortion(savesDir, zxLogOffset);
                if (portion.IndexOf(SetLevelCompletionMarker, StringComparison.Ordinal) >= 0)
                {
                    Log.Write("SETLEVEL VERIFY: '" + SetLevelCompletionMarker + "' present after " +
                              polls + " poll(s) - engine completed level setup (LoadLevel/minimap).");
                    return;
                }
                if (engineThreadDead)
                {
                    Log.Write("SETLEVEL VERIFY: engine thread died with no completion marker after " +
                              polls + " poll(s).");
                    DumpZxLogPortion(savesDir, zxLogOffset);
                    throw new Day0GenException("SetLevel verification failed: the engine thread died before " +
                                               "logging '" + SetLevelCompletionMarker + "' - level setup did not " +
                                               "complete. See dumped ZXLog portion above.");
                }
                Thread.Sleep(1000);
            }
            Log.Write("SETLEVEL VERIFY: TIMEOUT - '" + SetLevelCompletionMarker +
                      "' not in the post-SetLevel ZXLog after " + polls + " poll(s) (~90s).");
            DumpZxLogPortion(savesDir, zxLogOffset);
            throw new Day0GenException("SetLevel verification failed: engine did not log '" +
                                       SetLevelCompletionMarker + "' within 90s of SetLevel - level setup did " +
                                       "not complete engine-side. See dumped ZXLog portion above.");
        }

        // (b) the game system's current-level field must reference the exact
        // generated level object (identity, not equality).
        private static void VerifyCurrentLevelField(object sys, object level)
        {
            FieldInfo field = FindCurrentLevelField();
            object current = field.GetValue(sys);
            Log.Write("SETLEVEL VERIFY: current-level field " + field.Name + " -> " +
                      (current == null ? "null" : current.GetType().FullName) +
                      "; ReferenceEquals(generated level) = " + ReferenceEquals(current, level));
            if (!ReferenceEquals(current, level))
                throw new Day0GenException("SetLevel verification failed: the game system's current-level " +
                                           "field does not reference the generated level object - the engine did " +
                                           "not adopt the level; aborting before any save is written.");
        }

        // Current-level field locator: exact obfuscated name, then the fallback -
        // the ONLY instance field of the DXLevel type on the game system type (base
        // chain included). Zero or several candidates abort; no guess is made.
        private static FieldInfo FindCurrentLevelField()
        {
            string realName = GameReflector.Unescape(N_CURRENT_LEVEL_FIELD);
            FieldInfo exact = FindFieldUp(refl.GameSystemType, realName);
            if (exact != null)
            {
                Log.Write("FOUND [exact-name] game-system current-level field -> " +
                          (exact.DeclaringType != null ? exact.DeclaringType.FullName : "?") +
                          "." + realName + " : " + exact.FieldType.Name);
                return exact;
            }
            Log.Write("SETLEVEL VERIFY: current-level field exact-name lookup missed; falling back to " +
                      "the unique instance field typed " + refl.DxLevelType.FullName +
                      " on the game system type ...");
            List<FieldInfo> candidates = new List<FieldInfo>();
            for (Type t = refl.GameSystemType; t != null; t = t.BaseType)
            {
                FieldInfo[] fields;
                try
                {
                    fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { continue; }
                foreach (FieldInfo f in fields)
                {
                    if (f.FieldType == refl.DxLevelType) candidates.Add(f);
                }
            }
            if (candidates.Count == 1)
            {
                Log.Write("FOUND [typed-instance-field] game-system current-level field -> " +
                          (candidates[0].DeclaringType != null ? candidates[0].DeclaringType.FullName : "?") +
                          "." + candidates[0].Name);
                return candidates[0];
            }
            throw new Day0GenException("Game-system current-level field not resolvable: exact name '" +
                                       realName + "' missed and " + candidates.Count +
                                       " instance field(s) of type " + refl.DxLevelType.FullName +
                                       " found on " + refl.GameSystemType.FullName + " (need exactly 1)");
        }

        // ---------------------------------------------------------------------
        // Level adoption into ZXLevelState - the skipped SetLevel continuation.
        // 17:39 run: SetLevel's new-level branch NREs in per-entity scene-object
        // creation and the NRE ESCAPES at DXLevel.cs:438 - BEFORE the branch's next
        // statement, ZXLevelState.Current.#=zf9PbDap0F6OC(level) (decompile
        // --zxRcpu6e7NYzT7tGWqPjpOkc-.cs ~line 1851). That adoption is the game's own
        // day-0 start-state setup (ZXLevelState.cs 1597-1621): IDCurrentMission,
        // CurrentGeneratedLevel (what PreSave #=zQXHqcVh9mGZZ dereferences - the NRE
        // this step fixes), LevelEntities reset, LayerFog/LayerActivity, game time 0,
        // starting resources Gold += 100 / Wood += 20. Steps: (a) DXLevel.Current must
        // reference our level (the adoption dereferences it for the IsInProject gate
        // and the CurrentGeneratedLevel source); (b) run the game system's OnLoad on
        // our sys and verify DXSystem.Get<fogsys>() is non-null (17:53 run: the
        // adoption NRE'd 3ms in because the fog system did not exist - deferred
        // DXSystem.Load<gamesystem>(false) means OnLoad is engine-triggered in the
        // real game, and our out-of-band instance never got it); (c) invoke the
        // adoption on our ls; (d) verify the postconditions PreSave depends on.
        // ---------------------------------------------------------------------
        private static void AdoptLevelIntoLevelState(object ls, object sys, object level)
        {
            Log.Write("ADOPT: adopting the generated level into ZXLevelState (SetLevel continuation) ...");

            // (a) DXLevel.Current precondition.
            PropertyInfo curProp = FindDxLevelCurrentProp();
            FieldInfo curField = (curProp != null) ? null : FindDxLevelCurrentField();
            if (curProp == null && curField == null)
                throw new Day0GenException("DXLevel.Current is neither a static property with a setter " +
                                           "nor a writable static field - cannot set the adoption precondition; aborting.");
            object current = curProp != null
                ? refl.GetProp("DXLevel.Current (adopt precondition)", curProp, null)
                : curField.GetValue(null);
            if (current == null || !ReferenceEquals(current, level))
            {
                Log.Write("ADOPT: DXLevel.Current -> " +
                          (current == null ? "null" : current.GetType().FullName) +
                          " (!= our level); setting it to the generated level ...");
                if (curProp != null)
                    refl.SetProp("DXLevel.Current=level (adopt precondition)", curProp, null, level);
                else
                    curField.SetValue(null, level);
                object after = curProp != null
                    ? refl.GetProp("DXLevel.Current (adopt recheck)", curProp, null)
                    : curField.GetValue(null);
                Log.Write("ADOPT: DXLevel.Current after set -> " +
                          (after == null ? "null" : after.GetType().FullName));
                if (!ReferenceEquals(after, level))
                    throw new Day0GenException("Adoption precondition failed: DXLevel.Current still does not " +
                                               "reference the generated level after the set - aborting before any save is written.");
            }
            else
            {
                Log.Write("ADOPT: DXLevel.Current already references the generated level (precondition OK).");
            }

            // (b) Fog system: the adoption dereferences DXSystem.Get<fogsys>()
            // (ZXLevelState.cs ~1606) and that system is created by the game
            // system's OnLoad (DXSystem.Load<fogsys>(true)). We load the game
            // system deferred (DXSystem.Load<gamesystem>(false), like the real
            // survival click handler), so our out-of-band instance may not have
            // it. Invoke OnLoad ONLY when the fog system is actually missing:
            // OnLoad disposes the live survival/CC menu system and other
            // singletons, so it must only run on demand.
            MethodInfo fogGet = refl.DxSystemGetMethod.MakeGenericMethod(refl.FogSystemType);
            object fogSystem = refl.Invoke("DXSystem.Get<fogsys>() (pre-OnLoad)",
                fogGet, null);
            if (fogSystem != null)
            {
                Log.Write("ADOPT: fog system already present -> skipped OnLoad.");
            }
            else
            {
                Log.Write("ADOPT: fog system missing -> running game-system OnLoad ...");
                try
                {
                    refl.Invoke("gamesystem.OnLoad() [creates the fog system]",
                                refl.GameSystemOnLoadMethod, sys);
                }
                catch (Day0GenException e)
                {
                    LogExceptionChain("GAME SYSTEM ONLOAD", e);
                    throw new Day0GenException("Game-system OnLoad failed - the fog system the adoption " +
                                               "dereferences cannot be created; aborting before any save is written.", e);
                }

                // Re-fetch: null here is the exact precondition of the 17:53
                // adoption NRE (the adopt would NRE on the LayerFog fetch).
                fogSystem = refl.Invoke("DXSystem.Get<fogsys>() (post-OnLoad verify)",
                    fogGet, null);
            }
            Log.Write("ADOPT VERIFY: DXSystem.Get<fogsys>() -> " +
                      (fogSystem == null ? "null" : fogSystem.GetType().FullName));
            if (fogSystem == null)
                throw new Day0GenException("DXSystem.Get<fogsys>() is null after game-system OnLoad - " +
                                           "the fog system was not created and the adoption would NRE " +
                                           "(17:53 root cause); aborting before any save is written.");

            // (c) Invoke the adoption. The guard mirrors SetLevel's own branch
            // condition (the statement only runs when IDCurrentMission != level.ID):
            // on a CLEAN SetLevel path the engine already adopted, and invoking again
            // would double the day-0 starting resources (Gold += 100 / Wood += 20 a
            // second time).
            ulong levelId = ReadDxLevelId(level);
            PropertyInfo missionProp = refl.LevelStateType.GetProperty("IDCurrentMission");
            if (missionProp == null)
                throw new Day0GenException("Adoption failed: ZXLevelState.IDCurrentMission property not found (build drift?)");
            ulong missionId = Convert.ToUInt64(refl.GetProp(
                "ls.IDCurrentMission (adopt guard)", missionProp, ls));
            if (missionId == levelId)
            {
                Log.Write("ADOPT: IDCurrentMission (" + missionId.ToString() +
                          ") already equals level.ID - SetLevel's adoption ran engine-side; skipping the invoke.");
            }
            else
            {
                try
                {
                    refl.Invoke("ZXLevelState adopt(level) (SetLevel continuation)",
                                refl.AdoptLevelMethod, ls, level);
                }
                catch (Day0GenException e)
                {
                    // The phase-full FCE handler is registered and captures the IL
                    // offset when the adoption throws; this chain is the tool-side view.
                    LogExceptionChain("ADOPT LEVEL", e);
                    throw new Day0GenException("Level adoption into ZXLevelState failed - PreSave depends on " +
                                               "the adopted state (CurrentGeneratedLevel); aborting before any save is written.", e);
                }
            }

            // (d) Postconditions: the state PreSave dereferences.
            missionId = Convert.ToUInt64(refl.GetProp(
                "ls.IDCurrentMission (adopt verify)", missionProp, ls));
            Log.Write("ADOPT VERIFY: IDCurrentMission=" + missionId.ToString() +
                      ", level.ID=" + levelId.ToString() + ".");
            if (missionId != levelId)
                throw new Day0GenException("Adoption verify failed: IDCurrentMission (" + missionId.ToString() +
                                           ") != level.ID (" + levelId.ToString() +
                                           ") - the level was not adopted; aborting before any save is written.");

            PropertyInfo genProp = refl.LevelStateType.GetProperty("CurrentGeneratedLevel");
            if (genProp == null)
                throw new Day0GenException("Adoption verify failed: ZXLevelState.CurrentGeneratedLevel property not found (build drift?)");
            object generated = refl.GetProp("ls.CurrentGeneratedLevel (adopt verify)", genProp, ls);
            Log.Write("ADOPT VERIFY: CurrentGeneratedLevel ReferenceEquals(level) = " +
                      ReferenceEquals(generated, level) + ".");
            if (!ReferenceEquals(generated, level))
                throw new Day0GenException("Adoption verify failed: CurrentGeneratedLevel does not reference the " +
                                           "generated level (now: " + (generated == null ? "null" : generated.GetType().FullName) +
                                           ") - PreSave would NRE; aborting before any save is written.");

            PropertyInfo entitiesProp = refl.LevelStateType.GetProperty("LevelEntities");
            if (entitiesProp == null)
                throw new Day0GenException("Adoption verify failed: ZXLevelState.LevelEntities property not found (build drift?)");
            object entities = refl.GetProp("ls.LevelEntities (adopt verify)", entitiesProp, ls);
            Log.Write("ADOPT VERIFY: LevelEntities = " +
                      (entities == null ? "null (reset by adoption, as expected)" : "NON-NULL"));
            if (entities != null)
                throw new Day0GenException("Adoption verify failed: LevelEntities is not null - the adoption was " +
                                           "supposed to reset it; aborting before any save is written.");

            // Log-only (do NOT assert): the adoption is the game's day-0 setup, so the
            // starting resources (+100 gold / +20 wood) should be visible on top of
            // the constructor defaults.
            PropertyInfo goldProp = refl.LevelStateType.GetProperty("Gold");
            if (goldProp != null)
                Log.Write("ADOPT INFO: Gold = " + refl.GetProp("ls.Gold (adopt info)", goldProp, ls) +
                          " (day-0 setup adds +100; log only, not asserted)");
            else
                Log.Write("ADOPT INFO: Gold property not found (log only) - skipped");
            PropertyInfo woodProp = refl.LevelStateType.GetProperty("Wood");
            if (woodProp != null)
                Log.Write("ADOPT INFO: Wood = " + refl.GetProp("ls.Wood (adopt info)", woodProp, ls) +
                          " (day-0 setup adds +20; log only, not asserted)");
            else
                Log.Write("ADOPT INFO: Wood property not found (log only) - skipped");
        }

        // DXLevel.Current locator, property half. The adoption method DEREFERENCES
        // DXLevel.Current (IsInProject gate, CurrentGeneratedLevel source) and
        // SetLevel's own re-load branch assigns it directly ('DXLevel.Current =
        // level'), so a settable static member exists. Returns the settable static
        // property, or null (logged why).
        private static PropertyInfo FindDxLevelCurrentProp()
        {
            PropertyInfo p = refl.DxLevelType.GetProperty("Current",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null)
            {
                Log.Write("ADOPT: DXLevel.Current static property not found - trying static field ...");
                return null;
            }
            if (p.GetSetMethod(true) == null)
            {
                Log.Write("ADOPT: DXLevel.Current property has no setter - trying static field ...");
                return null;
            }
            Log.Write("FOUND [public-stable-name] DXLevel.Current settable static property -> " +
                      (p.DeclaringType != null ? p.DeclaringType.FullName : "?") + ".Current : " +
                      p.PropertyType.Name);
            return p;
        }

        // DXLevel.Current locator, field half (used only when the property is not
        // settable). Initonly (readonly) fields are not settable reflectively.
        private static FieldInfo FindDxLevelCurrentField()
        {
            FieldInfo f = refl.DxLevelType.GetField("Current",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
            {
                Log.Write("ADOPT: DXLevel.Current static field not found either.");
                return null;
            }
            if (f.IsInitOnly)
            {
                Log.Write("ADOPT: DXLevel.Current static field is initonly (readonly) - not settable.");
                return null;
            }
            Log.Write("FOUND [public-stable-name] DXLevel.Current static field -> " +
                      (f.DeclaringType != null ? f.DeclaringType.FullName : "?") + ".Current : " +
                      f.FieldType.Name + " (static, writable)");
            return f;
        }

        // DXLevel.ID accessor for the adoption guard/verify. DXLevel lives in the
        // embedded, non-decompilable DXVision assembly, so property-vs-field is
        // resolved at runtime on the level's concrete type (public "ID", numeric).
        private static ulong ReadDxLevelId(object level)
        {
            PropertyInfo p = level.GetType().GetProperty("ID");
            if (p != null)
                return Convert.ToUInt64(refl.GetProp("level.ID (adopt)", p, level));
            FieldInfo f = level.GetType().GetField("ID");
            if (f == null)
                throw new Day0GenException("Adoption failed: DXLevel exposes neither an ID property nor an ID field - cannot compare IDCurrentMission");
            Log.Write("ADOPT: level ID exposed as a FIELD (not property) - reading it directly.");
            return Convert.ToUInt64(f.GetValue(level));
        }

        // ---------------------------------------------------------------------
        // State re-assertion before the save. 17:35 run: after SetLevel completed
        // (Minimap OK) the engine's scene machine faded back to the START SCREEN
        // ("ZXSystem_StartScreen - ShowScene/ShowSceneSuccess") and its teardown
        // CLEARED ZXGameState.Current (and possibly ZXLevelState.Current /
        // manager.CurrentGameSystem). The SaveState wrapper reacts to a null
        // ZXGameState.Current with Thread.Sleep(2000) + silent return - no save,
        // no writer error (wrapper invoke 06.451, abort 08.454 = exactly the 2s
        // sleep). The tool still holds the constructed objects, so re-assert all
        // three references; a re-assert that does not stick aborts before the save.
        // ---------------------------------------------------------------------
        private static void ReAssertStateBeforeSave(object gs, object ls, object sys)
        {
            Log.Write("RE-ASSERT: checking DXLevel.Current / ZXGameState.Current / ZXLevelState.Current / " +
                      "manager.CurrentGameSystem before the save ...");
            bool gsReasserted = ReAssertStaticCurrent("ZXGameState",
                refl.GameStateCurrentMethod, refl.GameStateSetMethod, gs);
            bool lsReasserted = ReAssertStaticCurrent("ZXLevelState",
                refl.LevelStateCurrentMethod, refl.LevelStateSetMethod, ls);

            bool sysReasserted;
            {
                if (managerInstance == null)
                    throw new Day0GenException("Re-assert failed: managerInstance is null - cannot check manager.CurrentGameSystem");
                object current = refl.GetProp("manager.CurrentGameSystem (re-assert check)",
                    refl.CurrentGameSystemProp, managerInstance);
                sysReasserted = false;
                if (!ReferenceEquals(current, sys))
                {
                    Log.Write("RE-ASSERT: manager.CurrentGameSystem -> " +
                              (current == null ? "null" : current.GetType().FullName) +
                              " (!= our game system); re-asserting ...");
                    refl.SetProp("manager.CurrentGameSystem=sys (re-assert)",
                        refl.CurrentGameSystemProp, managerInstance, sys);
                    sysReasserted = true;
                    object after = refl.GetProp("manager.CurrentGameSystem (re-assert recheck)",
                        refl.CurrentGameSystemProp, managerInstance);
                    if (!ReferenceEquals(after, sys))
                        throw new Day0GenException("Re-assert failed: manager.CurrentGameSystem still does not " +
                            "reference our game system (now: " +
                            (after == null ? "null" : after.GetType().FullName) + ") - aborting before the save.");
                }
            }

            // C2 (review #3): DXLevel.Current is the gate PreSave branches on
            // (ZXLevelState.cs:1630: `if (DXLevel.Current == null) return;`). If the
            // post-SetLevel start-screen teardown nulls it, PreSave early-returns and
            // the save is structurally empty even though the three statics above are
            // fine. Same accessors as the adopt precondition; abort if it will not stick.
            bool dxLevelReasserted = false;
            {
                PropertyInfo levelProp = FindDxLevelCurrentProp();
                FieldInfo levelField = (levelProp != null) ? null : FindDxLevelCurrentField();
                if (levelProp == null && levelField == null)
                    throw new Day0GenException("Re-assert failed: DXLevel.Current is neither a settable static " +
                        "property nor a writable static field - cannot verify the PreSave precondition; " +
                        "aborting before the save.");
                object currentLevel = levelProp != null
                    ? refl.GetProp("DXLevel.Current (re-assert check)", levelProp, null)
                    : levelField.GetValue(null);
                if (!ReferenceEquals(currentLevel, dialogLevel))
                {
                    Log.Write("RE-ASSERT: DXLevel.Current -> " +
                              (currentLevel == null ? "null" : currentLevel.GetType().FullName) +
                              " (!= our level); re-asserting ...");
                    if (levelProp != null)
                        refl.SetProp("DXLevel.Current=level (re-assert)", levelProp, null, dialogLevel);
                    else
                        levelField.SetValue(null, dialogLevel);
                    dxLevelReasserted = true;
                    object afterLevel = levelProp != null
                        ? refl.GetProp("DXLevel.Current (re-assert recheck)", levelProp, null)
                        : levelField.GetValue(null);
                    if (!ReferenceEquals(afterLevel, dialogLevel))
                        throw new Day0GenException("Re-assert failed: DXLevel.Current still does not reference our " +
                            "level (now: " + (afterLevel == null ? "null" : afterLevel.GetType().FullName) +
                            ") - PreSave would early-return; aborting before the save.");
                }
            }

            Log.Write("RE-ASSERT: summary - DXLevel.Current " + (dxLevelReasserted ? "RE-ASSERTED" : "ok") +
                      ", ZXGameState.Current " + (gsReasserted ? "RE-ASSERTED" : "ok") +
                      ", ZXLevelState.Current " + (lsReasserted ? "RE-ASSERTED" : "ok") +
                      ", manager.CurrentGameSystem " + (sysReasserted ? "RE-ASSERTED" : "ok") + ".");
        }

        // Re-asserts one static ".Current" slot against the object we constructed.
        // Returns true when a re-assert was needed and it stuck; aborts when the
        // getter still does not return our object after the Set.
        private static bool ReAssertStaticCurrent(string label, MethodInfo currentGetter, MethodInfo setMethod, object ours)
        {
            object current = refl.Invoke(label + ".Current (re-assert check)", currentGetter, null);
            if (ReferenceEquals(current, ours))
                return false;
            Log.Write("RE-ASSERT: " + label + ".Current -> " +
                      (current == null ? "null" : current.GetType().FullName) +
                      " (!= our " + label + " instance); re-asserting via " + label + ".Set ...");
            refl.Invoke(label + ".Set(ours) (re-assert)", setMethod, null, ours);
            object after = refl.Invoke(label + ".Current (re-assert recheck)", currentGetter, null);
            if (!ReferenceEquals(after, ours))
                throw new Day0GenException("Re-assert failed: " + label + ".Current still does not reference " +
                    "our instance (now: " + (after == null ? "null" : after.GetType().FullName) +
                    ") - aborting before the save.");
            return true;
        }

        private static void ManualSave(string target, object gs)
        {
            // M1: last-resort writer - a null MethodBase would NRE inside the reflection
            // logging preamble; abort with a clear message instead.
            if (refl.ZipWriteMethod == null)
                throw new Day0GenException("ManualSave fallback unavailable: ZipSerializer.Write not discovered");
            if (refl.StateInfoMethod == null)
                throw new Day0GenException("ManualSave fallback unavailable: ZXGameState info-builder not discovered");
            if (refl.PwdSetMethod == null || refl.PwdClearMethod == null)
                throw new Day0GenException("ManualSave fallback unavailable: password set/clear method not discovered");
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

        // ---------------------------------------------------------------------
        // Command Center verification + diagnostics.
        //
        // ZXLevelState.PreSave rebuilds LevelEntities from the LIVE
        // DXGame.Current.ComponentsOfType<CSalvable>() registry after clearing the
        // generated level's entities (ZXLevelState.cs:1633-1657). The Command Center
        // is serialized only if it is present in that live registry; if it is not, the
        // save silently loads without one. AssertReadBackHasCommandCenter turns that
        // silent drop into a hard failure; LogCommandCenterDiagnostics is the
        // pre-save probe for WHERE the CC is (or is not).
        // ---------------------------------------------------------------------
        private static void AssertReadBackHasCommandCenter(object readBack)
        {
            PropertyInfo lsProp = FindPropertyUp(refl.GameStateType, "LevelState");
            if (lsProp == null)
                throw new Day0GenException("Read-back CC check: ZXGameState.LevelState property not found (build drift?)");
            object readLs = lsProp.GetValue(readBack, null);
            if (readLs == null)
                throw new Day0GenException("saved ZXGameState.LevelState is null - the save would load without a command center");
            PropertyInfo entitiesProp = refl.LevelStateType.GetProperty("LevelEntities");
            if (entitiesProp == null)
                throw new Day0GenException("Read-back CC check: ZXLevelState.LevelEntities property not found (build drift?)");
            object entities = entitiesProp.GetValue(readLs, null);
            int count = 0;
            bool ccPresent = false;
            if (entities != null)
            {
                System.Collections.IDictionary dict = entities as System.Collections.IDictionary;
                if (dict == null)
                    throw new Day0GenException("Read-back CC check: saved LevelEntities is not IDictionary (" +
                                               DescribeValue(entities) + ") - cannot verify the command center");
                foreach (object val in dict.Values)
                {
                    count++;
                    if (IsCommandCenter(val)) ccPresent = true;
                }
            }
            Log.Write("Read-back LevelEntities count=" + count + ", commandCenterPresent=" + ccPresent + ".");
            if (entities == null || count == 0 || !ccPresent)
                throw new Day0GenException("saved LevelEntities has no CommandCenter (count=" + count +
                    ", CC found=" + ccPresent + ") - the save would load without a command center");
        }

        // Diagnostics-only (never aborts): each probe is independently guarded - a
        // discovery/reflection failure logs "(skipped: ...)" and the next continues.
        private static void LogCommandCenterDiagnostics(object level)
        {
            // (a) live DXGame.Current.ComponentsOfType<CSalvable>() - the registry
            // PreSave rebuilds LevelEntities from.
            try
            {
                Type dxGameType = FindTypeAnyOrder("DXVision.DXGame", refl.DxAssembly);
                if (dxGameType == null)
                    throw new Day0GenException("DXVision.DXGame type not found");
                object current = GetStaticMemberValue(dxGameType, "Current");
                if (current == null)
                {
                    Log.Write("CC DIAG: live DXGame.Current is null (skipped: no game instance)");
                }
                else
                {
                    MethodInfo comps = FindComponentsOfTypeMethod(dxGameType);
                    if (comps == null)
                        throw new Day0GenException("DXGame.ComponentsOfType<T>() not found on DXGame/base chain");
                    Type csalvable = FindTypeAnyOrder("ZX.Components.CSalvable", refl.TabAssembly);
                    if (csalvable == null)
                        throw new Day0GenException("ZX.Components.CSalvable type not found");
                    MethodInfo closed = comps.MakeGenericMethod(csalvable);
                    object boxed = closed.Invoke(comps.IsStatic ? null : current, null);
                    int n = 0;
                    bool cc = false;
                    System.Collections.IEnumerable e = boxed as System.Collections.IEnumerable;
                    if (e != null)
                    {
                        foreach (object o in e)
                        {
                            n++;
                            if (IsCommandCenter(o)) cc = true;
                        }
                    }
                    Log.Write("CC DIAG: live DXGame CSalvable components = " + n +
                              ", CommandCenter present = " + cc);
                }
            }
            catch (Exception ex)
            {
                Log.Write("CC DIAG: live DXGame CSalvable components (skipped: " + DescribeException(ex) + ")");
            }

            // (b) generated level: Entities + Extension.MapDrawer.ExtraEntities.
            // The CC is created into ExtraEntities and moved into Entities by
            // SetLevel/UpdateLevel (ZXMapDrawer.cs:910/1420-1423, :1457).
            string entitiesDesc = "(skipped: not probed)";
            try { entitiesDesc = DescribeEntityCollection(ReadMemberValue(level, "Entities")); }
            catch (Exception ex) { entitiesDesc = "(skipped: " + DescribeException(ex) + ")"; }
            string extrasDesc = "(skipped: not probed)";
            try
            {
                object ext = ReadMemberValue(level, "Extension");
                if (ext == null)
                    throw new Day0GenException("DXLevel.Extension is null");
                object drawer = ReadMemberValue(ext, "MapDrawer");
                if (drawer == null)
                    throw new Day0GenException("ZXLevelExtension.MapDrawer is null");
                extrasDesc = DescribeEntityCollection(ReadMemberValue(drawer, "ExtraEntities"));
            }
            catch (Exception ex) { extrasDesc = "(skipped: " + DescribeException(ex) + ")"; }
            Log.Write("CC DIAG: generated level Entities=" + entitiesDesc + ", ExtraEntities=" + extrasDesc);

            // (c) ZXLevelState.Current.LevelEntities - null after the adopt by design.
            try
            {
                object currentLs = refl.LevelStateCurrentMethod != null
                    ? refl.LevelStateCurrentMethod.Invoke(null, null)
                    : null;
                object ents = null;
                if (currentLs != null)
                {
                    PropertyInfo p = refl.LevelStateType.GetProperty("LevelEntities");
                    if (p != null) ents = p.GetValue(currentLs, null);
                }
                Log.Write("CC DIAG: ZXLevelState.Current.LevelEntities = " + (ents == null ? "null" : "NON-NULL") +
                          " (null after the adopt by design; log only).");
            }
            catch (Exception ex)
            {
                Log.Write("CC DIAG: ZXLevelState.Current.LevelEntities (skipped: " + DescribeException(ex) + ")");
            }
        }

        // Discover ZX.Entities.CommandCenter by name and match by runtime type chain.
        // Falls back to a base-chain name check so a discovery miss cannot silently
        // pass an entity through.
        private static bool IsCommandCenter(object entity)
        {
            if (entity == null) return false;
            Type ccType = FindTypeAnyOrder("ZX.Entities.CommandCenter", refl.TabAssembly);
            if (ccType != null)
                return ccType.IsInstanceOfType(entity);
            for (Type t = entity.GetType(); t != null; t = t.BaseType)
            {
                if (t.FullName != null && t.FullName.EndsWith(".CommandCenter", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // Static member read for diagnostics: property (base chain) first, then field.
        private static object GetStaticMemberValue(Type t, string name)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                PropertyInfo p = null;
                try
                {
                    p = cur.GetProperty(name, BindingFlags.Static | BindingFlags.Public |
                                              BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (p != null && p.GetGetMethod(true) != null)
                    return p.GetValue(null, null);
                FieldInfo f = null;
                try
                {
                    f = cur.GetField(name, BindingFlags.Static | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (f != null) return f.GetValue(null);
            }
            return null;
        }

        // Instance member read for diagnostics: property (base chain) first, then field.
        private static object ReadMemberValue(object obj, string name)
        {
            if (obj == null)
                throw new Day0GenException("cannot read " + name + " from a null object");
            PropertyInfo p = FindPropertyUp(obj.GetType(), name);
            if (p != null) return p.GetValue(obj, null);
            FieldInfo f = FindFieldUp(obj.GetType(), name);
            if (f != null) return f.GetValue(obj);
            throw new Day0GenException(name + " member not found on " + obj.GetType().FullName);
        }

        // DXGame.ComponentsOfType<T>() discovery: static or instance, 0 args, generic
        // method definition, on DXGame or its base chain.
        private static MethodInfo FindComponentsOfTypeMethod(Type t)
        {
            for (Type cur = t; cur != null; cur = cur.BaseType)
            {
                MethodInfo[] ms = null;
                try
                {
                    ms = cur.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { }
                if (ms == null) continue;
                foreach (MethodInfo m in ms)
                {
                    if (m.Name != "ComponentsOfType") continue;
                    if (!m.IsGenericMethodDefinition) continue;
                    if (m.GetParameters().Length != 0) continue;
                    return m;
                }
            }
            return null;
        }

        private static string DescribeEntityCollection(object coll)
        {
            if (coll == null) return "null";
            System.Collections.IEnumerable e = coll as System.Collections.IEnumerable;
            if (e == null) return "not-enumerable(" + DescribeValue(coll) + ")";
            int n = 0;
            bool cc = false;
            foreach (object o in e)
            {
                n++;
                if (IsCommandCenter(o)) cc = true;
            }
            return "Count=" + n + ", CommandCenter=" + cc;
        }

        private static object ReadGameStateFromSave(string target)
        {
            // Replicates the game's load path: flag(path) -> set password -> read
            // "Data" entry -> clear. Read-only; used both for post-save read-back
            // and for save-mode param extraction.
            object flagObj = refl.Invoke("password flag(target)", refl.FlagMethod, null, target);
            int flag = (int)flagObj;
            refl.Invoke("password generator(set,read)", refl.PwdSetMethod, null, target, flag, true);
            // The game's own load path reads the ZIP "Data" entry via
            // ZipSerializer.Read(path, entry) after the password is set (manager
            // #=z3YFOTVtBw_rT7oSQRA==). Prefer it; ZXFile<ZXGameState>.read (the mod
            // reader) is only a fallback - it can return null where the ZIP reader
            // yields the state byte-for-byte as the game stored it.
            object state = null;
            Day0GenException zipReaderError = null;
            if (refl.ZipReadMethod != null)
            {
                try
                {
                    state = refl.Invoke("ZipSerializer.Read(target,'Data')", refl.ZipReadMethod, null, target, "Data");
                    Log.Write(state != null
                        ? "Read-back reader: ZipSerializer.Read(path,'Data') (game load path)."
                        : "Read-back reader: ZipSerializer.Read(path,'Data') returned null.");
                }
                catch (Day0GenException e)
                {
                    zipReaderError = e;
                    Log.Write("Read-back: ZipSerializer.Read(path,'Data') failed: " + e.Message);
                }
            }
            if (state == null && refl.ZxFileReadMethod != null)
            {
                state = refl.Invoke("ZXFile<ZXGameState>.read(target) [fallback]",
                                    refl.ZxFileReadMethod, null, target);
                Log.Write("Read-back reader: ZXFile<ZXGameState>.read fallback (ZipSerializer " +
                          (zipReaderError == null ? "returned null." : "failed.") + ")");
            }
            else if (state == null && zipReaderError != null)
            {
                throw zipReaderError;
            }
            if (refl.PwdClearMethod != null)
            {
                try { refl.Invoke("password generator(clear,read)", refl.PwdClearMethod, null, target, flag, true); }
                catch (Day0GenException e) { Log.Write("password clear after read failed (non-fatal): " + e.Message); }
            }
            return state;
        }
    }
}
