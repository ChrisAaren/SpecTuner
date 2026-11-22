using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using SpecTuner.Sidecar.Compilation;

namespace SpecTuner.Sidecar.Tests.Support;

/// <summary>
/// Compile-time stand-ins for a game's Managed folder: a tiny "UnityEngine" with just the
/// API surface the probe uses, plus .NET's own mscorlib/netstandard facades. Lets tests
/// compile the real probe source without Unity. Run-time behaviour is not tested this way.
/// </summary>
public static class UnityStub
{
    /// <summary>Facades and core library copied from the running .NET into fixture Managed folders.</summary>
    public static readonly string[] RuntimeFiles =
        ["mscorlib.dll", "netstandard.dll", "System.Runtime.dll", "System.Private.CoreLib.dll", "System.dll", "System.Core.dll", "System.Collections.dll", "System.Diagnostics.Process.dll", "System.ComponentModel.Primitives.dll"];

    public static string RuntimeDirectory => RuntimeEnvironment.GetRuntimeDirectory();

    private static readonly Lazy<byte[]> LazyAssembly = new(() =>
    {
        var references = new[] { "System.Runtime.dll", "System.Private.CoreLib.dll" }
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(RuntimeDirectory, f)))
            .ToList();
        var result = PluginCompiler.Compile("UnityEngine", [new SourceFile("UnityEngine.cs", Source)], references);
        if (!result.Success)
            throw new InvalidOperationException("Unity stub failed to compile: " + string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Assembly!;
    });

    public static byte[] Assembly => LazyAssembly.Value;

    private const string Source = """
        using System.Collections;
        namespace UnityEngine
        {
            public class Object { public string name { get; set; } }
            public class Component : Object { }
            public class Behaviour : Component { }
            public sealed class Coroutine { }
            public class MonoBehaviour : Behaviour { public Coroutine StartCoroutine(IEnumerator routine) { return null; } }
            public sealed class WaitForSecondsRealtime { public WaitForSecondsRealtime(float time) { } }
            public static class Application
            {
                public static bool runInBackground { get; set; }
                public static int targetFrameRate { get; set; }
                public static string unityVersion { get { return "6000.0.0f1"; } }
                public static void Quit() { }
                public static void OpenURL(string url) { }
                public static void CaptureScreenshot(string filename) { }
                public static object RequestUserAuthorization(int mode) { return null; }
            }
            public static class ScreenCapture { public static void CaptureScreenshot(string filename) { } }
            public class AssetBundle
            {
                public static AssetBundle LoadFromFile(string path) { return null; }
                public static object LoadFromFileAsync(string path) { return null; }
            }
            public static class Caching { public static bool ClearCache() { return true; } }
            public sealed class Ping { public Ping(string address) { } }
            public static class Network { public static void Disconnect() { } }
            public sealed class NetworkView { }
            public static class MasterServer { public static void ClearHostList() { } }
            public sealed class WebCamTexture { }
            public static class Microphone { public static string[] devices { get { return new string[0]; } } }
            public class WWW { public WWW(string url) { } }
            public class WWWForm { }
            public enum ShadowQuality { Disable, HardOnly, All }
            public enum ShadowResolution { Low, Medium, High, VeryHigh }
            public enum AnisotropicFiltering { Disable, Enable, ForceEnable }
            public enum FullScreenMode { ExclusiveFullScreen, FullScreenWindow, MaximizedWindow, Windowed }
            public static class QualitySettings
            {
                public static int vSyncCount { get; set; }
                public static int GetQualityLevel() { return 0; }
                public static void SetQualityLevel(int index, bool applyExpensiveChanges) { }
                public static string[] names { get { return new string[0]; } }
                public static ShadowQuality shadows { get; set; }
                public static float shadowDistance { get; set; }
                public static ShadowResolution shadowResolution { get; set; }
                public static int shadowCascades { get; set; }
                public static int antiAliasing { get; set; }
                public static AnisotropicFiltering anisotropicFiltering { get; set; }
                public static float lodBias { get; set; }
                public static int maximumLODLevel { get; set; }
                public static int pixelLightCount { get; set; }
                public static bool softParticles { get; set; }
                public static bool realtimeReflectionProbes { get; set; }
            }
            public struct Resolution { public int width { get; set; } public int height { get; set; } }
            public static class Screen
            {
                public static int width { get { return 0; } }
                public static int height { get { return 0; } }
                public static FullScreenMode fullScreenMode { get; set; }
                public static Resolution currentResolution { get { return default(Resolution); } }
                public static void SetResolution(int width, int height, FullScreenMode mode) { }
            }
            public static class SystemInfo
            {
                public static string graphicsDeviceName { get { return ""; } }
                public static int graphicsMemorySize { get { return 0; } }
                public static Rendering.GraphicsDeviceType graphicsDeviceType { get { return 0; } }
                public static string processorType { get { return ""; } }
                public static int processorCount { get { return 0; } }
                public static int systemMemorySize { get { return 0; } }
                public static string operatingSystem { get { return ""; } }
            }
            public static class Time
            {
                public static float unscaledDeltaTime { get { return 0; } }
                public static float realtimeSinceStartup { get { return 0; } }
            }
        }
        namespace UnityEngine.Networking { public class UnityWebRequest { public static UnityWebRequest Get(string uri) { return null; } } }
        namespace UnityEngine.Rendering { public enum GraphicsDeviceType { Direct3D11 } }
        namespace UnityEngine.Windows
        {
            public static class File { public static byte[] ReadAllBytes(string path) { return null; } }
            public static class Directory { public static void CreateDirectory(string path) { } }
        }
        namespace UnityEngine.Windows.WebCam { public sealed class PhotoCapture { } }
        namespace UnityEngine.Windows.Speech { public sealed class DictationRecognizer { } }
        namespace UnityEngine.Profiling { public static class Profiler { public static string logFile { get; set; } } }
        namespace UnityEngine.Diagnostics { public static class Utils { public static void ForceCrash(int mode) { } } }
        namespace UnityEngine.Events { public delegate void UnityAction<T0, T1>(T0 arg0, T1 arg1); }
        namespace UnityEngine.SceneManagement
        {
            public struct Scene { public string name { get; set; } public bool isLoaded { get; set; } }
            public enum LoadSceneMode { Single, Additive }
            public static class SceneManager
            {
                public static event UnityEngine.Events.UnityAction<Scene, LoadSceneMode> sceneLoaded;
                public static Scene GetActiveScene() { return default(Scene); }
            }
        }
        """;
}
