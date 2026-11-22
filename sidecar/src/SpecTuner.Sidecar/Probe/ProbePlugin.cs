// SpecTuner probe: a BepInEx 5 plugin compiled per game by the sidecar at
// install time (see Loader/ProbeInstaller.cs), against that game's own Unity
// assemblies. This file is embedded in the sidecar, not compiled into it.
//
// Long-stable Unity APIs are used directly. Anything that differs between
// Unity versions or render pipelines (URP/HDRP asset properties, the mipmap
// limit, refresh rate) is reached through reflection, so the probe compiles
// against any Unity the game ships. Everything is changed in memory only;
// no game file is ever written.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpecTuner.Probe
{
    [BepInPlugin("com.spectuner.probe", "SpecTuner Probe", "1.0.0")]
    public sealed class ProbePlugin : BaseUnityPlugin
    {
        // Applied in this order: the quality level first, because it resets the others.
        private static readonly string[] KeyOrder =
        {
            "qualityLevel", "resolutionWidth", "resolutionHeight", "fullscreenMode", "vSyncCount", "targetFrameRate",
            "renderScale", "shadows", "shadowDistance", "shadowResolution", "shadowCascades", "antiAliasing",
            "anisotropicFiltering", "textureMipmapLimit", "lodBias", "maximumLODLevel", "pixelLightCount",
            "softParticles", "realtimeReflectionProbes",
        };

        private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const float DriftCheckSeconds = 1f;

        private string _outDir;
        private string _configDir;
        private Dictionary<string, object> _overrides = new Dictionary<string, object>();
        private readonly Dictionary<string, int> _reapplied = new Dictionary<string, int>();
        private bool _startupDone;
        private float _nextDriftCheck;
        private bool _updateErrorLogged;

        private enum Phase { Off, WaitingForScene, Warmup, Measure, Trial, Done }
        private Phase _phase = Phase.Off;
        private string _runId;
        private float _warmupSeconds;
        private float _measureSeconds;
        private bool _quitWhenDone;
        private float _phaseStart;
        private readonly List<float> _frames = new List<float>(16384);
        private bool _sceneLoaded;
        private string _sceneName = "";
        private int _gameVSyncCount = int.MinValue;
        private int _gameTargetFrameRate = int.MinValue;
        private int _capReasserts;

        // A/B trial inside one launch: A = saved overrides (plus the game's own values for the
        // tried keys), B = saved overrides plus the tried settings. Windows alternate A,B,A,B...
        private Dictionary<string, object> _savedOverrides;
        private Dictionary<string, object> _trySettings;
        private Dictionary<string, object> _stateA;
        private Dictionary<string, object> _stateB;
        private int _pairs;
        private float _windowSeconds;
        private float _settleSeconds;
        private int _window;
        private bool _settling;
        private readonly List<List<float>> _windowFrames = new List<List<float>>();
        private readonly Dictionary<string, bool> _trialConfirmed = new Dictionary<string, bool>();
        private readonly JObj _trialActual = new JObj();

        // Setup waits for Start, when the engine is fully running, rather than Awake,
        // which can run while BepInEx is still starting inside an engine static constructor.
        private void Start()
        {
            try
            {
                _outDir = Path.Combine(Paths.BepInExRootPath, "spectuner");
                _configDir = Paths.ConfigPath;
                Directory.CreateDirectory(_outDir);
                _overrides = LoadOverrides();
                LoadBenchmarkRequest();
                SceneManager.sceneLoaded += OnSceneLoaded;
                Logger.LogInfo("SpecTuner probe started: " + _overrides.Count + " quality override(s)" +
                               (_runId != null ? ", benchmark " + _runId : ""));
            }
            catch (Exception e)
            {
                Logger.LogError("SpecTuner probe failed to start: " + e);
                return;
            }
            StartCoroutine(StartupPasses());
        }

        private IEnumerator StartupPasses()
        {
            yield return null;
            try { WriteReports(); }
            catch (Exception e) { Logger.LogError("SpecTuner: could not write the hardware report: " + e.Message); }
            ApplyOverrides("startup", false);
            yield return new WaitForSecondsRealtime(3f);
            ApplyOverrides("startup + 3 s", false);
            _startupDone = true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _sceneLoaded = true;
            _sceneName = scene.name;
            StartCoroutine(SceneLoadPasses(scene.name));
        }

        private IEnumerator SceneLoadPasses(string scene)
        {
            ApplyOverrides("scene " + scene, false);
            yield return null; // after the scene's own Start() methods
            ApplyOverrides("scene " + scene + ", next frame", false);
            yield return new WaitForSecondsRealtime(3f);
            ApplyOverrides("scene " + scene + " + 3 s", false);
        }

        private void Update()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_startupDone && _overrides.Count > 0 && now >= _nextDriftCheck)
                {
                    _nextDriftCheck = now + DriftCheckSeconds;
                    ApplyOverrides("check", true);
                }

                switch (_phase)
                {
                    case Phase.WaitingForScene:
                        if (_startupDone && (_sceneLoaded || SceneManager.GetActiveScene().isLoaded))
                        {
                            if (_sceneName.Length == 0) _sceneName = SceneManager.GetActiveScene().name;
                            _gameVSyncCount = QualitySettings.vSyncCount;
                            _gameTargetFrameRate = Application.targetFrameRate;
                            ApplyOverrides("benchmark start", false);
                            LiftFrameCap();
                            _phase = Phase.Warmup;
                            _phaseStart = now;
                            Logger.LogInfo("SpecTuner: benchmark " + _runId + " warming up for " + _warmupSeconds + " s in scene '" + _sceneName +
                                           "' (game had vSyncCount=" + _gameVSyncCount + ", targetFrameRate=" + _gameTargetFrameRate + ")");
                        }
                        break;
                    case Phase.Warmup:
                        LiftFrameCap();
                        if (now - _phaseStart >= _warmupSeconds)
                        {
                            if (_trySettings != null)
                            {
                                BeginTrial(now);
                                break;
                            }
                            _phase = Phase.Measure;
                            _phaseStart = now;
                            _frames.Clear();
                        }
                        break;
                    case Phase.Trial:
                        LiftFrameCap();
                        UpdateTrial(now);
                        break;
                    case Phase.Measure:
                        LiftFrameCap();
                        _frames.Add(Time.unscaledDeltaTime);
                        if (now - _phaseStart >= _measureSeconds)
                            FinishBenchmark(_frames, FrameStats.WindowFps(_frames, 4), null);
                        break;
                }
            }
            catch (Exception e)
            {
                if (!_updateErrorLogged)
                    Logger.LogError("SpecTuner probe error: " + e);
                _updateErrorLogged = true;
            }
        }

        private void LateUpdate()
        {
            if (Measuring)
                LiftFrameCap();
        }

        // Measurement only: never written to the overrides file.
        private void LiftFrameCap()
        {
            Application.runInBackground = true;
            if (QualitySettings.vSyncCount != 0)
            {
                QualitySettings.vSyncCount = 0;
                _capReasserts++;
            }
            if (Application.targetFrameRate != -1)
            {
                Application.targetFrameRate = -1;
                _capReasserts++;
            }
        }

        private bool Measuring
        {
            get { return _phase == Phase.Warmup || _phase == Phase.Measure || _phase == Phase.Trial; }
        }

        private static bool IsFrameCapKey(string key)
        {
            return key == "vSyncCount" || key == "targetFrameRate";
        }

        // ---------------------------------------------------------------- overrides

        private Dictionary<string, object> LoadOverrides()
        {
            var result = new Dictionary<string, object>();
            string path = Path.Combine(_configDir, "spectuner.quality.json");
            if (!File.Exists(path))
                return result;
            var parsed = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            if (parsed == null)
                return result;
            foreach (var kv in parsed)
            {
                if (Array.IndexOf(KeyOrder, kv.Key) < 0)
                    Logger.LogWarning("SpecTuner: ignoring unknown setting '" + kv.Key + "'");
                else if (kv.Value != null)
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        private void ApplyOverrides(string pass, bool isDriftCheck)
        {
            if (_overrides.Count == 0)
                return;
            int changed = 0;
            bool resolutionDone = false;
            foreach (var key in KeyOrder)
            {
                object desired;
                if (!_overrides.TryGetValue(key, out desired))
                    continue;
                if (IsFrameCapKey(key) && Measuring)
                    continue;
                try
                {
                    if (key == "resolutionWidth" || key == "resolutionHeight" || key == "fullscreenMode")
                    {
                        if (resolutionDone)
                            continue;
                        resolutionDone = true;
                        if (ApplyResolution(pass, isDriftCheck))
                            changed++;
                        continue;
                    }
                    object current = Read(key);
                    if (current != null && Matches(current, desired))
                        continue;
                    if (!Apply(key, desired))
                    {
                        if (!isDriftCheck)
                            Logger.LogWarning("SpecTuner: " + pass + ": " + key + " is not supported by this game");
                        continue;
                    }
                    changed++;
                    Report(pass, isDriftCheck, key, current, desired);
                }
                catch (Exception e)
                {
                    Logger.LogWarning("SpecTuner: " + pass + ": could not apply " + key + ": " + e.Message);
                }
            }
            if (changed == 0 && !isDriftCheck)
                Logger.LogInfo("SpecTuner: " + pass + ": all overrides already in effect");
        }

        private void Report(string pass, bool isDriftCheck, string key, object before, object desired)
        {
            if (!isDriftCheck)
            {
                Logger.LogInfo("SpecTuner: " + pass + ": " + key + " " + Show(before) + " -> " + Show(desired));
                return;
            }
            int count;
            _reapplied.TryGetValue(key, out count);
            _reapplied[key] = count + 1;
            if (count < 3)
                Logger.LogInfo("SpecTuner: the game changed " + key + " to " + Show(before) + "; set it back to " + Show(desired));
        }

        private bool ApplyResolution(string pass, bool isDriftCheck)
        {
            object w, h, m;
            bool hasSize = _overrides.TryGetValue("resolutionWidth", out w) & _overrides.TryGetValue("resolutionHeight", out h);
            bool hasMode = _overrides.TryGetValue("fullscreenMode", out m);
            FullScreenMode mode = hasMode ? (FullScreenMode)Enum.Parse(typeof(FullScreenMode), (string)m, true) : Screen.fullScreenMode;
            if (hasSize)
            {
                int width = ToInt(w), height = ToInt(h);
                if (Screen.width == width && Screen.height == height && Screen.fullScreenMode == mode)
                    return false;
                string before = Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode;
                Screen.SetResolution(width, height, mode);
                Report(pass, isDriftCheck, "resolution", before, width + "x" + height + " " + mode);
                return true;
            }
            if (hasMode && Screen.fullScreenMode != mode)
            {
                string before = Screen.fullScreenMode.ToString();
                Screen.fullScreenMode = mode;
                Report(pass, isDriftCheck, "fullscreenMode", before, mode.ToString());
                return true;
            }
            return false;
        }

        /// <summary>The value in effect now, or null when this game has no such setting.</summary>
        private object Read(string key)
        {
            object asset = PipelineAsset();
            object v;
            switch (key)
            {
                case "qualityLevel": return QualitySettings.GetQualityLevel();
                case "vSyncCount": return QualitySettings.vSyncCount;
                case "targetFrameRate": return Application.targetFrameRate;
                case "resolutionWidth": return Screen.width;
                case "resolutionHeight": return Screen.height;
                case "fullscreenMode": return Screen.fullScreenMode.ToString();
                case "renderScale": return Get(asset, "renderScale");
                case "shadows":
                    v = Get(asset, "supportsMainLightShadows", "m_MainLightShadowsSupported");
                    if (v != null)
                    {
                        if (!(bool)v) return "Disable";
                        object soft = Get(asset, "supportsSoftShadows", "m_SoftShadowsSupported");
                        return soft != null && (bool)soft ? "All" : "HardOnly";
                    }
                    return QualitySettings.shadows.ToString();
                case "shadowDistance":
                    return Get(asset, "shadowDistance") ?? (object)QualitySettings.shadowDistance;
                case "shadowResolution":
                    v = Get(asset, "mainLightShadowmapResolution", "m_MainLightShadowmapResolution");
                    if (v != null) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
                    switch (QualitySettings.shadowResolution)
                    {
                        case ShadowResolution.Low: return 512;
                        case ShadowResolution.Medium: return 1024;
                        case ShadowResolution.High: return 2048;
                        default: return 4096;
                    }
                case "shadowCascades":
                    v = Get(asset, "shadowCascadeCount", "m_ShadowCascadeCount");
                    if (v != null) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
                    return QualitySettings.shadowCascades <= 1 ? 1 : QualitySettings.shadowCascades;
                case "antiAliasing":
                    v = Get(asset, "msaaSampleCount", "m_MSAA");
                    if (v != null)
                    {
                        int samples = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                        return samples <= 1 ? 0 : samples;
                    }
                    return QualitySettings.antiAliasing;
                case "anisotropicFiltering": return QualitySettings.anisotropicFiltering.ToString();
                case "textureMipmapLimit": return GetOn(typeof(QualitySettings), null, "globalTextureMipmapLimit", "masterTextureLimit");
                case "lodBias": return QualitySettings.lodBias;
                case "maximumLODLevel": return QualitySettings.maximumLODLevel;
                case "pixelLightCount": return Get(asset, "maxAdditionalLightsCount") ?? (object)QualitySettings.pixelLightCount;
                case "softParticles": return QualitySettings.softParticles;
                case "realtimeReflectionProbes": return QualitySettings.realtimeReflectionProbes;
            }
            return null;
        }

        /// <summary>Sets a value in memory. False when this game has no such setting.</summary>
        private bool Apply(string key, object value)
        {
            object asset = PipelineAsset();
            switch (key)
            {
                case "qualityLevel":
                    QualitySettings.SetQualityLevel(ToInt(value), true);
                    return true;
                case "vSyncCount":
                    QualitySettings.vSyncCount = ToInt(value);
                    return true;
                case "targetFrameRate":
                    Application.targetFrameRate = ToInt(value);
                    return true;
                case "renderScale":
                    return Set(asset, ToFloat(value), "renderScale");
                case "shadows":
                {
                    var quality = (ShadowQuality)Enum.Parse(typeof(ShadowQuality), (string)value, true);
                    QualitySettings.shadows = quality;
                    if (asset != null)
                    {
                        Set(asset, quality != ShadowQuality.Disable, "supportsMainLightShadows", "m_MainLightShadowsSupported");
                        Set(asset, quality != ShadowQuality.Disable, "supportsAdditionalLightShadows", "m_AdditionalLightShadowsSupported");
                        Set(asset, quality == ShadowQuality.All, "supportsSoftShadows", "m_SoftShadowsSupported");
                    }
                    return true;
                }
                case "shadowDistance":
                    QualitySettings.shadowDistance = ToFloat(value);
                    if (asset != null) Set(asset, ToFloat(value), "shadowDistance");
                    return true;
                case "shadowResolution":
                {
                    int px = ToInt(value);
                    QualitySettings.shadowResolution = px <= 512 ? ShadowResolution.Low
                        : px <= 1024 ? ShadowResolution.Medium
                        : px <= 2048 ? ShadowResolution.High : ShadowResolution.VeryHigh;
                    if (asset != null)
                    {
                        Set(asset, px, "mainLightShadowmapResolution", "m_MainLightShadowmapResolution");
                        Set(asset, px, "additionalLightsShadowmapResolution", "m_AdditionalLightsShadowmapResolution");
                    }
                    return true;
                }
                case "shadowCascades":
                {
                    int n = ToInt(value);
                    QualitySettings.shadowCascades = n <= 1 ? 0 : n == 2 ? 2 : 4;
                    if (asset != null) Set(asset, n, "shadowCascadeCount", "m_ShadowCascadeCount");
                    return true;
                }
                case "antiAliasing":
                {
                    int n = ToInt(value);
                    QualitySettings.antiAliasing = n;
                    if (asset != null) Set(asset, Math.Max(1, n), "msaaSampleCount", "m_MSAA");
                    return true;
                }
                case "anisotropicFiltering":
                    QualitySettings.anisotropicFiltering = (AnisotropicFiltering)Enum.Parse(typeof(AnisotropicFiltering), (string)value, true);
                    return true;
                case "textureMipmapLimit":
                    return SetOn(typeof(QualitySettings), null, ToInt(value), "globalTextureMipmapLimit", "masterTextureLimit");
                case "lodBias":
                    QualitySettings.lodBias = ToFloat(value);
                    return true;
                case "maximumLODLevel":
                    QualitySettings.maximumLODLevel = ToInt(value);
                    return true;
                case "pixelLightCount":
                    QualitySettings.pixelLightCount = ToInt(value);
                    if (asset != null) Set(asset, ToInt(value), "maxAdditionalLightsCount");
                    return true;
                case "softParticles":
                    QualitySettings.softParticles = (bool)value;
                    return true;
                case "realtimeReflectionProbes":
                    QualitySettings.realtimeReflectionProbes = (bool)value;
                    return true;
            }
            return false;
        }

        private static bool Matches(object current, object desired)
        {
            if (current is bool || desired is bool)
                return current is bool && desired is bool && (bool)current == (bool)desired;
            if (current is string || desired is string)
                return string.Equals(Convert.ToString(current, CultureInfo.InvariantCulture), Convert.ToString(desired, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            double a = Convert.ToDouble(current, CultureInfo.InvariantCulture);
            double b = Convert.ToDouble(desired, CultureInfo.InvariantCulture);
            return Math.Abs(a - b) <= 0.001 * Math.Max(1.0, Math.Abs(b));
        }

        // ---------------------------------------------------------------- reflection

        /// <summary>The active render pipeline asset (URP/HDRP), or null for the built-in pipeline.</summary>
        private static object PipelineAsset()
        {
            Type graphics = typeof(QualitySettings).Assembly.GetType("UnityEngine.Rendering.GraphicsSettings");
            object asset = GetOn(graphics, null, "currentRenderPipeline")
                           ?? GetOn(typeof(QualitySettings), null, "renderPipeline")
                           ?? GetOn(graphics, null, "renderPipelineAsset");
            var unityObject = asset as UnityEngine.Object;
            return unityObject != null ? asset : null;
        }

        private static object Get(object target, params string[] names)
        {
            return target == null ? null : GetOn(target.GetType(), target, names);
        }

        private static object GetOn(Type type, object target, params string[] names)
        {
            if (type == null)
                return null;
            foreach (var name in names)
            {
                var property = type.GetProperty(name, AnyMember);
                if (property != null && property.GetGetMethod(true) != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(target, null);
                var field = type.GetField(name, AnyMember);
                if (field != null)
                    return field.GetValue(target);
            }
            return null;
        }

        private static bool Set(object target, object value, params string[] names)
        {
            return target != null && SetOn(target.GetType(), target, value, names);
        }

        private static bool SetOn(Type type, object target, object value, params string[] names)
        {
            if (type == null)
                return false;
            foreach (var name in names)
            {
                var property = type.GetProperty(name, AnyMember);
                if (property != null && property.GetSetMethod(true) != null && property.GetIndexParameters().Length == 0)
                {
                    property.SetValue(target, ConvertTo(value, property.PropertyType), null);
                    return true;
                }
                var field = type.GetField(name, AnyMember);
                if (field != null && !field.IsInitOnly)
                {
                    field.SetValue(target, ConvertTo(value, field.FieldType));
                    return true;
                }
            }
            return false;
        }

        private static object ConvertTo(object value, Type type)
        {
            if (type.IsEnum)
                return value is string ? Enum.Parse(type, (string)value, true) : Enum.ToObject(type, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        private static int ToInt(object value)
        {
            return Convert.ToInt32(Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
        }

        private static float ToFloat(object value)
        {
            return (float)Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static string Show(object value)
        {
            if (value == null) return "(none)";
            if (value is float || value is double) return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- reports

        private void WriteReports()
        {
            WriteJson("hardware.json", Hardware());
            var settings = Settings();
            settings.Add("qualityLevelNames", QualitySettings.names);
            WriteJson("settings.json", settings);
        }

        private static JObj Hardware()
        {
            var screen = Screen.currentResolution;
            return new JObj
            {
                { "gpu", SystemInfo.graphicsDeviceName },
                { "gpuMemoryMb", SystemInfo.graphicsMemorySize },
                { "graphicsApi", SystemInfo.graphicsDeviceType.ToString() },
                { "cpu", SystemInfo.processorType == null ? null : SystemInfo.processorType.Trim() },
                { "cores", SystemInfo.processorCount },
                { "ramMb", SystemInfo.systemMemorySize },
                { "os", SystemInfo.operatingSystem },
                { "screen", screen.width + "x" + screen.height },
                { "refreshRateHz", RefreshRate(screen) },
            };
        }

        private static object RefreshRate(Resolution resolution)
        {
            object boxed = resolution;
            object ratio = GetOn(boxed.GetType(), boxed, "refreshRateRatio"); // Unity 2022.2+
            if (ratio != null)
            {
                object value = GetOn(ratio.GetType(), ratio, "value");
                if (value != null)
                    return Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), 2);
            }
            return GetOn(boxed.GetType(), boxed, "refreshRate");
        }

        private JObj Settings()
        {
            object asset = PipelineAsset();
            int level = QualitySettings.GetQualityLevel();
            string[] names = QualitySettings.names;
            var unityObject = asset as UnityEngine.Object;
            return new JObj
            {
                { "qualityLevel", level },
                { "qualityLevelName", level >= 0 && level < names.Length ? names[level] : null },
                { "vSyncCount", QualitySettings.vSyncCount },
                { "targetFrameRate", Application.targetFrameRate },
                { "resolution", Screen.width + "x" + Screen.height },
                { "fullscreenMode", Screen.fullScreenMode.ToString() },
                { "renderPipeline", unityObject == null ? "Built-in" : asset.GetType().Name + " (" + unityObject.name + ")" },
                { "renderScale", Read("renderScale") },
                { "shadows", Read("shadows") },
                { "shadowDistance", Read("shadowDistance") },
                { "shadowResolution", Read("shadowResolution") },
                { "shadowCascades", Read("shadowCascades") },
                { "antiAliasing", Read("antiAliasing") },
                { "anisotropicFiltering", Read("anisotropicFiltering") },
                { "textureMipmapLimit", Read("textureMipmapLimit") },
                { "lodBias", Read("lodBias") },
            };
        }

        private void WriteJson(string fileName, object value)
        {
            string path = Path.Combine(_outDir, fileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, MiniJson.Write(value));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        // ---------------------------------------------------------------- benchmark

        private void LoadBenchmarkRequest()
        {
            string path = Path.Combine(_configDir, "spectuner.benchmark.json");
            if (!File.Exists(path))
                return;
            var request = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            // Consumed: a request never survives into a later, normal launch.
            File.Delete(path);
            if (request == null)
                return;

            double createdAt = Number(request, "createdAtUnix", 0);
            double nowUnix = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            if (Math.Abs(nowUnix - createdAt) > 600)
            {
                Logger.LogWarning("SpecTuner: ignoring an old benchmark request");
                return;
            }

            object runId;
            request.TryGetValue("runId", out runId);
            _runId = runId as string ?? "run";
            _warmupSeconds = (float)Number(request, "warmupSeconds", 10);
            _measureSeconds = (float)Number(request, "measureSeconds", 20);
            object quit;
            _quitWhenDone = request.TryGetValue("quitWhenDone", out quit) && quit is bool && (bool)quit;

            object trialValue;
            var trial = request.TryGetValue("trial", out trialValue) ? trialValue as Dictionary<string, object> : null;
            object trySettings;
            if (trial != null && trial.TryGetValue("settings", out trySettings) && trySettings is Dictionary<string, object>)
            {
                _trySettings = new Dictionary<string, object>();
                foreach (var kv in (Dictionary<string, object>)trySettings)
                    if (Array.IndexOf(KeyOrder, kv.Key) >= 0 && kv.Value != null && !IsFrameCapKey(kv.Key))
                        _trySettings[kv.Key] = kv.Value;
                _pairs = Math.Max(1, (int)Number(trial, "pairs", 4));
                _windowSeconds = (float)Number(trial, "windowSeconds", 5);
                _settleSeconds = (float)Number(trial, "settleSeconds", 2);
            }
            _phase = Phase.WaitingForScene;
            Application.runInBackground = true;
        }

        private static double Number(Dictionary<string, object> map, string key, double fallback)
        {
            object value;
            return map.TryGetValue(key, out value) && value is double ? (double)value : fallback;
        }

        // ---------------------------------------------------------------- A/B trial

        private void BeginTrial(float now)
        {
            _savedOverrides = _overrides;
            _stateA = new Dictionary<string, object>(_savedOverrides);
            _stateB = new Dictionary<string, object>(_savedOverrides);
            foreach (var kv in _trySettings)
            {
                // A keeps the game's own value for every tried key, so switching back really undoes B.
                if (!_stateA.ContainsKey(kv.Key))
                {
                    object original = Read(kv.Key);
                    if (original != null)
                        _stateA[kv.Key] = original;
                }
                _stateB[kv.Key] = kv.Value;
                _trialConfirmed[kv.Key] = true;
            }
            _phase = Phase.Trial;
            _window = 0;
            Logger.LogInfo("SpecTuner: benchmark " + _runId + " trial of " + string.Join(", ", new List<string>(_trySettings.Keys).ToArray()) +
                           ": " + _pairs + " pairs of " + _windowSeconds + " s windows, " + _settleSeconds + " s settle");
            SwitchTrialState(now);
        }

        private void SwitchTrialState(float now)
        {
            bool isB = _window % 2 == 1;
            _overrides = isB ? _stateB : _stateA;
            ApplyOverrides("trial " + (isB ? "B" : "A") + (_window / 2 + 1), false);
            _settling = true;
            _phaseStart = now;
        }

        private void UpdateTrial(float now)
        {
            if (_settling)
            {
                if (now - _phaseStart >= _settleSeconds)
                {
                    _settling = false;
                    _phaseStart = now;
                    _windowFrames.Add(new List<float>(4096));
                }
                return;
            }

            _windowFrames[_windowFrames.Count - 1].Add(Time.unscaledDeltaTime);
            if (now - _phaseStart < _windowSeconds)
                return;

            if (_window % 2 == 1)
                CheckTrialKeys();
            _window++;
            if (_window < 2 * _pairs)
            {
                SwitchTrialState(now);
                return;
            }

            _overrides = _stateA;
            ApplyOverrides("trial end, back to A", false);
            _overrides = _savedOverrides;
            FinishTrial();
        }

        private void CheckTrialKeys()
        {
            _trialActual.Clear();
            foreach (var kv in _trySettings)
            {
                object current = Read(kv.Key);
                _trialActual.Add(kv.Key, current);
                if (current == null || !Matches(current, kv.Value))
                    _trialConfirmed[kv.Key] = false;
            }
        }

        private void FinishTrial()
        {
            var aFrames = new List<float>();
            var bFrames = new List<float>();
            var windows = new List<object>();
            for (int i = 0; i < _windowFrames.Count; i++)
            {
                bool isB = i % 2 == 1;
                if (isB) bFrames.AddRange(_windowFrames[i]);
                else aFrames.AddRange(_windowFrames[i]);
                var w = FrameStats.From(_windowFrames[i]);
                windows.Add(new JObj { { "state", isB ? "B" : "A" }, { "avgFps", w.AvgFps }, { "low1PctFps", w.Low1PctFps }, { "frames", w.Frames } });
            }
            var a = FrameStats.From(aFrames);
            var b = FrameStats.From(bFrames);
            var inEffect = new JObj();
            foreach (var kv in _trialConfirmed)
                inEffect.Add(kv.Key, kv.Value);

            var trial = new JObj
            {
                { "settings", _trySettings },
                { "pairs", _pairs },
                { "windowSeconds", _windowSeconds },
                { "settleSeconds", _settleSeconds },
                { "a", Summary(a) },
                { "b", Summary(b) },
                { "windows", windows },
                { "inEffect", inEffect },
                { "actual", _trialActual },
            };
            Logger.LogInfo("SpecTuner: trial A " + a.AvgFps.ToString("0.0", CultureInfo.InvariantCulture) + " fps, B " +
                           b.AvgFps.ToString("0.0", CultureInfo.InvariantCulture) + " fps");
            FinishBenchmark(aFrames, null, trial);
        }

        private static JObj Summary(FrameStats stats)
        {
            return new JObj
            {
                { "avgFps", stats.AvgFps }, { "low1PctFps", stats.Low1PctFps }, { "p50Ms", stats.P50Ms }, { "p95Ms", stats.P95Ms }, { "frames", stats.Frames },
            };
        }

        // The top-level numbers are the A windows for a trial, the whole measurement otherwise.
        private void FinishBenchmark(List<float> frames, double[] subWindowFps, JObj trial)
        {
            _phase = Phase.Done;
            var stats = FrameStats.From(frames);

            var requested = new JObj();
            var inEffect = new JObj();
            var actual = new JObj();
            var unsupported = new List<string>();
            var suspended = new List<string>();
            foreach (var key in KeyOrder)
            {
                object desired;
                if (!_overrides.TryGetValue(key, out desired))
                    continue;
                requested.Add(key, desired);
                if (IsFrameCapKey(key))
                {
                    suspended.Add(key);
                    continue;
                }
                object current = Read(key);
                actual.Add(key, current);
                if (current == null)
                    unsupported.Add(key);
                inEffect.Add(key, current != null && Matches(current, desired));
            }
            var reapplied = new JObj();
            foreach (var kv in _reapplied)
                reapplied.Add(kv.Key, kv.Value);

            var result = new JObj
            {
                { "runId", _runId },
                { "unityVersion", Application.unityVersion },
                { "scene", _sceneName },
                { "frames", stats.Frames },
                { "seconds", stats.Seconds },
                { "avgFps", stats.AvgFps },
                { "low1PctFps", stats.Low1PctFps },
                { "frameMs", new JObj { { "avg", stats.AvgMs }, { "p50", stats.P50Ms }, { "p95", stats.P95Ms }, { "p99", stats.P99Ms }, { "max", stats.MaxMs } } },
                { "resolution", Screen.width + "x" + Screen.height },
                { "settings", Settings() },
                { "hardware", Hardware() },
                { "overrides", new JObj
                    {
                        { "requested", requested }, { "inEffect", inEffect }, { "actual", actual },
                        { "reapplied", reapplied }, { "unsupported", unsupported }, { "suspendedForMeasurement", suspended },
                    }
                },
                { "subWindowFps", subWindowFps },
                { "trial", trial },
                { "frameCap", new JObj
                    {
                        { "gameVSyncCount", _gameVSyncCount }, { "gameTargetFrameRate", _gameTargetFrameRate }, { "reasserted", _capReasserts },
                    }
                },
            };
            WriteJson("benchmark-" + SafeFileName(_runId) + ".json", result);
            Logger.LogInfo("SpecTuner: benchmark " + _runId + " done: " + stats.Frames + " frames, " +
                           stats.AvgFps.ToString("0.0", CultureInfo.InvariantCulture) + " fps average, " +
                           stats.Low1PctFps.ToString("0.0", CultureInfo.InvariantCulture) + " fps 1% low");
            if (_quitWhenDone)
                Application.Quit();
        }

        private static string SafeFileName(string id)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                id = id.Replace(c, '_');
            return id;
        }
    }
}
