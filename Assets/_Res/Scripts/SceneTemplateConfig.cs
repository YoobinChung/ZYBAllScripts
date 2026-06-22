using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[CreateAssetMenu(fileName = "SceneTemplateConfig", menuName = "Config/Scene Template Config")]
public class SceneTemplateConfig : ScriptableObject
{
    [Header("Apply Options")]
    public bool createMissingObjects = true;
    public bool applyTransforms = true;

    [Header("Scene Settings")]
    public RenderSettingsConfig renderSettings = new RenderSettingsConfig();
    public LightmapSettingsConfig lightmapSettings = new LightmapSettingsConfig();

    [Header("Scene Objects")]
    public CameraConfig[] cameras = Array.Empty<CameraConfig>();
    public LightConfig[] lights = Array.Empty<LightConfig>();
    public VolumeConfig[] volumes = Array.Empty<VolumeConfig>();

    public void SaveFromCurrentScene()
    {
        renderSettings.Save();
        lightmapSettings.Save();

        cameras = CaptureComponents<Camera, CameraConfig>(CameraConfig.FromCamera);
        lights = CaptureComponents<Light, LightConfig>(LightConfig.FromLight);
        volumes = CaptureVolumes();
    }

    public void ApplyToCurrentScene()
    {
        renderSettings.Apply();
        lightmapSettings.Apply();

        ApplyConfigs(cameras, config => config.Apply(createMissingObjects, applyTransforms));
        ApplyConfigs(lights, config => config.Apply(createMissingObjects, applyTransforms));
        ApplyConfigs(volumes, config => config.Apply(createMissingObjects, applyTransforms));
    }

    static TConfig[] CaptureComponents<TComponent, TConfig>(Func<TComponent, TConfig> factory)
        where TComponent : Component
    {
        var components = FindObjectsByType<TComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var configs = new TConfig[components.Length];

        for (int i = 0; i < components.Length; i++)
        {
            configs[i] = factory(components[i]);
        }

        return configs;
    }

    static VolumeConfig[] CaptureVolumes()
    {
        Type volumeType = FindType("UnityEngine.Rendering.Volume");
        if (volumeType == null)
        {
            return Array.Empty<VolumeConfig>();
        }

        var components = FindSceneComponentsByType(volumeType);
        var configs = new VolumeConfig[components.Count];

        for (int i = 0; i < components.Count; i++)
        {
            configs[i] = VolumeConfig.FromVolume(components[i]);
        }

        return configs;
    }

    static List<Component> FindSceneComponentsByType(Type componentType)
    {
        var result = new List<Component>();
        var objects = Resources.FindObjectsOfTypeAll(componentType);

        for (int i = 0; i < objects.Length; i++)
        {
            var component = objects[i] as Component;
            if (component != null && component.gameObject.scene.IsValid())
            {
                result.Add(component);
            }
        }

        return result;
    }

    static void ApplyConfigs<TConfig>(IEnumerable<TConfig> configs, Action<TConfig> apply)
    {
        if (configs == null)
        {
            return;
        }

        foreach (var config in configs)
        {
            apply(config);
        }
    }

    public static string GetPath(Transform transform)
    {
        if (transform == null)
        {
            return string.Empty;
        }

        var names = new Stack<string>();
        Transform current = transform;

        while (current != null)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names);
    }

    public static GameObject FindObjectByPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var activeScene = SceneManager.GetActiveScene();
        var roots = activeScene.GetRootGameObjects();

        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i].name == path)
            {
                return roots[i];
            }

            if (path.StartsWith(roots[i].name + "/", StringComparison.Ordinal))
            {
                string childPath = path.Substring(roots[i].name.Length + 1);
                Transform child = roots[i].transform.Find(childPath);
                if (child != null)
                {
                    return child.gameObject;
                }
            }
        }

        return null;
    }

    public static GameObject FindObjectByName(string objectName)
    {
        if (string.IsNullOrEmpty(objectName))
        {
            return null;
        }

        var transforms = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i].name == objectName)
            {
                return transforms[i].gameObject;
            }
        }

        return null;
    }

    public static GameObject FindOrCreateObject(string path, string fallbackName, bool createMissing)
    {
        GameObject gameObject = FindObjectByPath(path);
        if (gameObject != null || !createMissing)
        {
            return gameObject;
        }

        if (string.IsNullOrEmpty(path))
        {
            string objectName = string.IsNullOrEmpty(fallbackName) ? "Scene Template Object" : fallbackName;
            return new GameObject(objectName);
        }

        string[] names = path.Split('/');
        Transform parent = null;
        GameObject current = null;

        for (int i = 0; i < names.Length; i++)
        {
            if (string.IsNullOrEmpty(names[i]))
            {
                continue;
            }

            current = parent == null ? FindRootObject(names[i]) : FindChildObject(parent, names[i]);

            if (current == null)
            {
                current = new GameObject(names[i]);
                if (parent != null)
                {
                    current.transform.SetParent(parent, false);
                }
            }

            parent = current.transform;
        }

        return current;
    }

    static GameObject FindRootObject(string objectName)
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i].name == objectName)
            {
                return roots[i];
            }
        }

        return null;
    }

    static GameObject FindChildObject(Transform parent, string objectName)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == objectName)
            {
                return child.gameObject;
            }
        }

        return null;
    }

    public static void ApplyTransform(Transform target, TransformConfig config)
    {
        if (target == null)
        {
            return;
        }

        target.position = config.position;
        target.rotation = config.rotation;
        target.localScale = config.localScale;
    }

    static Type FindType(string fullName)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type type = assemblies[i].GetType(fullName);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    static Component GetComponentByType(GameObject target, Type componentType)
    {
        return target != null && componentType != null ? target.GetComponent(componentType) : null;
    }

    static Component AddComponentByType(GameObject target, Type componentType)
    {
        return target != null && componentType != null ? target.AddComponent(componentType) : null;
    }

    static T GetReflectedValue<T>(object target, string propertyName, T fallback = default)
    {
        if (target == null)
        {
            return fallback;
        }

        var property = target.GetType().GetProperty(propertyName);
        if (property == null || !property.CanRead)
        {
            return fallback;
        }

        try
        {
            object value = property.GetValue(target, null);
            return value is T typedValue ? typedValue : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    static void SetReflectedValue(object target, string propertyName, object value)
    {
        if (target == null)
        {
            return;
        }

        var property = target.GetType().GetProperty(propertyName);
        if (property != null && property.CanWrite)
        {
            try
            {
                property.SetValue(target, value, null);
            }
            catch (Exception)
            {
            }
        }
    }

    [Serializable]
    public class TransformConfig
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;

        public static TransformConfig FromTransform(Transform transform)
        {
            return new TransformConfig
            {
                position = transform.position,
                rotation = transform.rotation,
                localScale = transform.localScale
            };
        }
    }

    [Serializable]
    public class RenderSettingsConfig
    {
        [Header("Skybox")]
        public Material skybox;
        public string sunLightPath;

        [Header("Ambient")]
        public AmbientMode ambientMode = AmbientMode.Skybox;
        public Color ambientLight = Color.gray;
        public Color ambientSkyColor = Color.gray;
        public Color ambientEquatorColor = Color.gray;
        public Color ambientGroundColor = Color.gray;
        public float ambientIntensity = 1f;

        [Header("Reflection")]
        public DefaultReflectionMode defaultReflectionMode = DefaultReflectionMode.Skybox;
        public int defaultReflectionResolution = 128;
        public int reflectionBounces = 1;
        public float reflectionIntensity = 1f;
        public Cubemap customReflection;

        [Header("Fog")]
        public bool fog;
        public FogMode fogMode = FogMode.ExponentialSquared;
        public Color fogColor = Color.gray;
        public float fogDensity = 0.01f;
        public float fogStartDistance;
        public float fogEndDistance = 300f;

        [Header("Other")]
        public Color subtractiveShadowColor = Color.gray;
        public float haloStrength = 0.5f;
        public float flareStrength = 1f;
        public float flareFadeSpeed = 3f;

        public void Save()
        {
            skybox = RenderSettings.skybox;
            sunLightPath = RenderSettings.sun != null ? GetPath(RenderSettings.sun.transform) : string.Empty;

            ambientMode = RenderSettings.ambientMode;
            ambientLight = RenderSettings.ambientLight;
            ambientSkyColor = RenderSettings.ambientSkyColor;
            ambientEquatorColor = RenderSettings.ambientEquatorColor;
            ambientGroundColor = RenderSettings.ambientGroundColor;
            ambientIntensity = RenderSettings.ambientIntensity;

            defaultReflectionMode = RenderSettings.defaultReflectionMode;
            defaultReflectionResolution = RenderSettings.defaultReflectionResolution;
            reflectionBounces = RenderSettings.reflectionBounces;
            reflectionIntensity = RenderSettings.reflectionIntensity;
            customReflection = GetCustomReflectionSafe();

            fog = RenderSettings.fog;
            fogMode = RenderSettings.fogMode;
            fogColor = RenderSettings.fogColor;
            fogDensity = RenderSettings.fogDensity;
            fogStartDistance = RenderSettings.fogStartDistance;
            fogEndDistance = RenderSettings.fogEndDistance;

            subtractiveShadowColor = RenderSettings.subtractiveShadowColor;
            haloStrength = RenderSettings.haloStrength;
            flareStrength = RenderSettings.flareStrength;
            flareFadeSpeed = RenderSettings.flareFadeSpeed;
        }

        public void Apply()
        {
            RenderSettings.skybox = skybox;

            GameObject sunObject = FindObjectByPath(sunLightPath);
            RenderSettings.sun = sunObject != null ? sunObject.GetComponent<Light>() : null;

            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
            RenderSettings.ambientSkyColor = ambientSkyColor;
            RenderSettings.ambientEquatorColor = ambientEquatorColor;
            RenderSettings.ambientGroundColor = ambientGroundColor;
            RenderSettings.ambientIntensity = ambientIntensity;

            RenderSettings.defaultReflectionMode = defaultReflectionMode;
            RenderSettings.defaultReflectionResolution = defaultReflectionResolution;
            RenderSettings.reflectionBounces = reflectionBounces;
            RenderSettings.reflectionIntensity = reflectionIntensity;
            RenderSettings.customReflection = customReflection;

            RenderSettings.fog = fog;
            RenderSettings.fogMode = fogMode;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.fogStartDistance = fogStartDistance;
            RenderSettings.fogEndDistance = fogEndDistance;

            RenderSettings.subtractiveShadowColor = subtractiveShadowColor;
            RenderSettings.haloStrength = haloStrength;
            RenderSettings.flareStrength = flareStrength;
            RenderSettings.flareFadeSpeed = flareFadeSpeed;
        }

        static Cubemap GetCustomReflectionSafe()
        {
            try
            {
                return RenderSettings.customReflection;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    [Serializable]
    public class LightmapSettingsConfig
    {
        public LightingSettings lightingSettings;
        public LightmapsMode lightmapsMode = LightmapsMode.NonDirectional;
        public LightProbes lightProbes;
        public LightmapDataConfig[] lightmaps = Array.Empty<LightmapDataConfig>();

        public void Save()
        {
#if UNITY_EDITOR
            lightingSettings = GetLightingSettingsSafe();
#endif
            lightmapsMode = LightmapSettings.lightmapsMode;
            lightProbes = LightmapSettings.lightProbes;

            var sourceLightmaps = LightmapSettings.lightmaps;
            lightmaps = new LightmapDataConfig[sourceLightmaps.Length];
            for (int i = 0; i < sourceLightmaps.Length; i++)
            {
                lightmaps[i] = LightmapDataConfig.FromLightmapData(sourceLightmaps[i]);
            }
        }

        public void Apply()
        {
#if UNITY_EDITOR
            ApplyLightingSettingsSafe(lightingSettings);
#endif
            LightmapSettings.lightmapsMode = lightmapsMode;
            LightmapSettings.lightProbes = lightProbes;

            if (lightmaps == null)
            {
                LightmapSettings.lightmaps = Array.Empty<LightmapData>();
                return;
            }

            var targetLightmaps = new LightmapData[lightmaps.Length];
            for (int i = 0; i < lightmaps.Length; i++)
            {
                targetLightmaps[i] = lightmaps[i].ToLightmapData();
            }

            LightmapSettings.lightmaps = targetLightmaps;
        }

#if UNITY_EDITOR
        static LightingSettings GetLightingSettingsSafe()
        {
            try
            {
                return UnityEditor.Lightmapping.lightingSettings;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void ApplyLightingSettingsSafe(LightingSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            UnityEditor.Lightmapping.lightingSettings = settings;
        }
#endif
    }

    [Serializable]
    public class LightmapDataConfig
    {
        public Texture2D lightmapColor;
        public Texture2D lightmapDir;
        public Texture2D shadowMask;

        public static LightmapDataConfig FromLightmapData(LightmapData source)
        {
            return new LightmapDataConfig
            {
                lightmapColor = source.lightmapColor,
                lightmapDir = source.lightmapDir,
                shadowMask = source.shadowMask
            };
        }

        public LightmapData ToLightmapData()
        {
            return new LightmapData
            {
                lightmapColor = lightmapColor,
                lightmapDir = lightmapDir,
                shadowMask = shadowMask
            };
        }
    }

    [Serializable]
    public class CameraConfig
    {
        public string path;
        public string objectName;

        [Header("Camera")]
        public CameraClearFlags clearFlags = CameraClearFlags.Skybox;
        public Color backgroundColor = Color.black;
        public UniversalCameraConfig universal = new UniversalCameraConfig();

        public static CameraConfig FromCamera(Camera source)
        {
            return new CameraConfig
            {
                path = GetPath(source.transform),
                objectName = source.name,
                clearFlags = source.clearFlags,
                backgroundColor = source.backgroundColor,
                universal = UniversalCameraConfig.FromCamera(source)
            };
        }

        public void Apply(bool createMissing, bool shouldApplyTransform)
        {
            GameObject target = FindOrCreateObject(path, objectName, createMissing);
            if (target == null)
            {
                return;
            }

            var camera = target.GetComponent<Camera>();
            if (camera == null && createMissing)
            {
                camera = target.AddComponent<Camera>();
            }

            if (camera == null)
            {
                return;
            }

            camera.clearFlags = clearFlags;
            camera.backgroundColor = backgroundColor;
            universal.Apply(target, createMissing);
        }
    }

    [Serializable]
    public class LightConfig
    {
        public string path;
        public string objectName;
        public bool active;
        public bool enabled;
        public TransformConfig transform = new TransformConfig();

        [Header("Light")]
        public LightType type = LightType.Directional;
        public LightShape shape = LightShape.Cone;
        public Color color = Color.white;
        public float colorTemperature = 6570f;
        public bool useColorTemperature;
        public float intensity = 1f;
        public float bounceIntensity = 1f;
        public float range = 10f;
        public float spotAngle = 30f;
        public float innerSpotAngle;
        public LightShadows shadows = LightShadows.None;
        public float shadowStrength = 1f;
        public float shadowBias = 0.05f;
        public float shadowNormalBias = 0.4f;
        public float shadowNearPlane = 0.2f;
        public LightRenderMode renderMode = LightRenderMode.Auto;
        public LightmapBakeType lightmapBakeType = LightmapBakeType.Realtime;
        public int cullingMask = -1;
        public UniversalLightConfig universal = new UniversalLightConfig();

        public static LightConfig FromLight(Light source)
        {
            return new LightConfig
            {
                path = GetPath(source.transform),
                objectName = source.name,
                active = source.gameObject.activeSelf,
                enabled = source.enabled,
                transform = TransformConfig.FromTransform(source.transform),
                type = source.type,
                shape = source.shape,
                color = source.color,
                colorTemperature = source.colorTemperature,
                useColorTemperature = source.useColorTemperature,
                intensity = source.intensity,
                bounceIntensity = source.bounceIntensity,
                range = source.range,
                spotAngle = source.spotAngle,
                innerSpotAngle = source.innerSpotAngle,
                shadows = source.shadows,
                shadowStrength = source.shadowStrength,
                shadowBias = source.shadowBias,
                shadowNormalBias = source.shadowNormalBias,
                shadowNearPlane = source.shadowNearPlane,
                renderMode = source.renderMode,
                lightmapBakeType = source.lightmapBakeType,
                cullingMask = source.cullingMask,
                universal = UniversalLightConfig.FromLight(source)
            };
        }

        public void Apply(bool createMissing, bool shouldApplyTransform)
        {
            GameObject target = FindOrCreateObject(path, objectName, createMissing);
            if (target == null)
            {
                return;
            }

            var light = target.GetComponent<Light>();
            if (light == null && createMissing)
            {
                light = target.AddComponent<Light>();
            }

            if (light == null)
            {
                return;
            }

            target.SetActive(active);
            light.enabled = enabled;

            if (shouldApplyTransform)
            {
                ApplyTransform(target.transform, transform);
            }

            light.type = type;
            light.shape = shape;
            light.color = color;
            light.colorTemperature = colorTemperature;
            light.useColorTemperature = useColorTemperature;
            light.intensity = intensity;
            light.bounceIntensity = bounceIntensity;
            light.range = range;
            light.spotAngle = spotAngle;
            light.innerSpotAngle = innerSpotAngle;
            light.shadows = shadows;
            light.shadowStrength = shadowStrength;
            light.shadowBias = shadowBias;
            light.shadowNormalBias = shadowNormalBias;
            light.shadowNearPlane = shadowNearPlane;
            light.renderMode = renderMode;
            light.lightmapBakeType = lightmapBakeType;
            light.cullingMask = cullingMask;
            universal.Apply(target, createMissing);
        }
    }

    [Serializable]
    public class UniversalCameraConfig
    {
        public bool hasData;
        public bool renderPostProcessing;

        public static UniversalCameraConfig FromCamera(Camera source)
        {
            var config = new UniversalCameraConfig();
            Type dataType = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            var data = GetComponentByType(source.gameObject, dataType);
            if (data == null)
            {
                return config;
            }

            config.hasData = true;
            config.renderPostProcessing = GetReflectedValue(data, "renderPostProcessing", false);
            return config;
        }

        public void Apply(GameObject target, bool createMissing)
        {
            if (!hasData || target == null)
            {
                return;
            }

            Type dataType = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            var data = GetComponentByType(target, dataType);
            if (data == null && createMissing)
            {
                data = AddComponentByType(target, dataType);
            }

            if (data == null)
            {
                return;
            }

            SetReflectedValue(data, "renderPostProcessing", renderPostProcessing);
        }
    }

    [Serializable]
    public class UniversalLightConfig
    {
        public bool hasData;
        public bool usePipelineSettings = true;
        public uint renderingLayers = 1;
        public bool customShadowLayers;
        public uint shadowRenderingLayers = 1;
        public Vector2 lightCookieSize = Vector2.one;
        public Vector2 lightCookieOffset;

        public static UniversalLightConfig FromLight(Light source)
        {
            var config = new UniversalLightConfig();
            Type dataType = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalLightData");
            var data = GetComponentByType(source.gameObject, dataType);
            if (data == null)
            {
                return config;
            }

            config.hasData = true;
            config.usePipelineSettings = GetReflectedValue(data, "usePipelineSettings", true);
            config.renderingLayers = GetReflectedValue<uint>(data, "renderingLayers", 1);
            config.customShadowLayers = GetReflectedValue(data, "customShadowLayers", false);
            config.shadowRenderingLayers = GetReflectedValue<uint>(data, "shadowRenderingLayers", 1);
            config.lightCookieSize = GetReflectedValue(data, "lightCookieSize", Vector2.one);
            config.lightCookieOffset = GetReflectedValue(data, "lightCookieOffset", Vector2.zero);
            return config;
        }

        public void Apply(GameObject target, bool createMissing)
        {
            if (!hasData || target == null)
            {
                return;
            }

            Type dataType = FindType("UnityEngine.Rendering.Universal.UniversalAdditionalLightData");
            var data = GetComponentByType(target, dataType);
            if (data == null && createMissing)
            {
                data = AddComponentByType(target, dataType);
            }

            if (data == null)
            {
                return;
            }

            SetReflectedValue(data, "usePipelineSettings", usePipelineSettings);
            SetReflectedValue(data, "renderingLayers", renderingLayers);
            SetReflectedValue(data, "customShadowLayers", customShadowLayers);
            SetReflectedValue(data, "shadowRenderingLayers", shadowRenderingLayers);
            SetReflectedValue(data, "lightCookieSize", lightCookieSize);
            SetReflectedValue(data, "lightCookieOffset", lightCookieOffset);
        }
    }

    [Serializable]
    public class VolumeConfig
    {
        public string path;
        public string objectName;
        public bool active;
        public bool enabled;
        public TransformConfig transform = new TransformConfig();

        [Header("Volume")]
        public ScriptableObject profile;
        public bool isGlobal = true;
        public float priority;
        public float blendDistance;
        public float weight = 1f;

        public static VolumeConfig FromVolume(Component source)
        {
            return new VolumeConfig
            {
                path = GetPath(source.transform),
                objectName = source.name,
                active = source.gameObject.activeSelf,
                enabled = source is Behaviour behaviour && behaviour.enabled,
                transform = TransformConfig.FromTransform(source.transform),
                profile = GetReflectedValue<ScriptableObject>(source, "sharedProfile"),
                isGlobal = GetReflectedValue(source, "isGlobal", true),
                priority = GetReflectedValue(source, "priority", 0f),
                blendDistance = GetReflectedValue(source, "blendDistance", 0f),
                weight = GetReflectedValue(source, "weight", 1f)
            };
        }

        public void Apply(bool createMissing, bool shouldApplyTransform)
        {
            GameObject target = FindOrCreateObject(path, objectName, createMissing);
            if (target == null)
            {
                return;
            }

            Type volumeType = FindType("UnityEngine.Rendering.Volume");
            var volume = GetComponentByType(target, volumeType);
            if (volume == null && createMissing)
            {
                volume = AddComponentByType(target, volumeType);
            }

            if (volume == null)
            {
                return;
            }

            target.SetActive(active);
            if (volume is Behaviour behaviour)
            {
                behaviour.enabled = enabled;
            }

            if (shouldApplyTransform)
            {
                ApplyTransform(target.transform, transform);
            }

            SetReflectedValue(volume, "sharedProfile", profile);
            SetReflectedValue(volume, "isGlobal", isGlobal);
            SetReflectedValue(volume, "priority", priority);
            SetReflectedValue(volume, "blendDistance", blendDistance);
            SetReflectedValue(volume, "weight", weight);
        }
    }

}
