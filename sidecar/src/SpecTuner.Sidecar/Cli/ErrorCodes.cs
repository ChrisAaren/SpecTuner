namespace SpecTuner.Sidecar.Cli;

public static class ErrorCodes
{
    // Request handling
    public const string UnknownCommand = "UNKNOWN_COMMAND";
    public const string InvalidJson = "INVALID_JSON";
    public const string BadRequest = "BAD_REQUEST";
    public const string InternalError = "INTERNAL_ERROR";
    public const string Busy = "BUSY";

    // Scope gate
    public const string GameNotFound = "GAME_NOT_FOUND";
    public const string NotUnity = "NOT_UNITY";
    public const string Il2CppNotSupported = "IL2CPP_NOT_SUPPORTED";
    public const string NotWindowsX64 = "NOT_WINDOWS_X64";
    public const string AntiCheat = "ANTI_CHEAT";
    public const string ProtectedCode = "PROTECTED_CODE";
    public const string OnlineMultiplayer = "ONLINE_MULTIPLAYER";

    // Changing the game
    public const string GameRunning = "GAME_RUNNING";
    public const string LoaderConflict = "LOADER_CONFLICT";
    public const string UnsupportedLoader = "UNSUPPORTED_LOADER";
    public const string ChangeNotFound = "CHANGE_NOT_FOUND";
    public const string ChangeNotActive = "CHANGE_NOT_ACTIVE";
    public const string ChangeStillActive = "CHANGE_STILL_ACTIVE";
    public const string CannotReapply = "CANNOT_REAPPLY";
    public const string RevertBlocked = "REVERT_BLOCKED";
    public const string ProbeBuildFailed = "PROBE_BUILD_FAILED";
    public const string InvalidSetting = "INVALID_SETTING";

    // Running the game
    public const string GameFailedToStart = "GAME_FAILED_TO_START";
    public const string LogNotFound = "LOG_NOT_FOUND";

    // Game code and mods
    public const string AssemblyNotFound = "ASSEMBLY_NOT_FOUND";
    public const string TypeNotFound = "TYPE_NOT_FOUND";
    public const string AmbiguousType = "AMBIGUOUS_TYPE";
    public const string MemberNotFound = "MEMBER_NOT_FOUND";
    public const string CompileFailed = "COMPILE_FAILED";
    public const string InvalidPlugin = "INVALID_PLUGIN";
    public const string UnsafeCode = "UNSAFE_CODE";
    public const string ModFailedTest = "MOD_FAILED_TEST";
}
