#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

public sealed class OSMCityBuilderWindow : EditorWindow
{
    private const string RootName = "OSM_City_Generated";
    private const string OverpassUrl = "https://overpass-api.de/api/interpreter";
    private const string DefaultAssetFolder = "Assets/GeneratedOSM";

    private enum AreaMode
    {
        CircleRadius,
        BoundingBox
    }

    private enum PointKind
    {
        Generic,
        Tree,
        Bench,
        Fountain,
        Well,
        Lamp,
        Monument,
        Info
    }

    [Serializable]
    private sealed class TextureOverrideEntry
    {
        public long OsmId;
        public Texture2D FacadeTexture;
        public Texture2D RoofTexture;
    }

    private sealed class MaterialPair
    {
        public Material Facade;
        public Material Roof;
    }

    private sealed class GeneratedBuildingItem
    {
        public long SourceId;
        public List<long> NodeIds;
        public Dictionary<string, string> Tags;
        public bool HasTextureOverride;
    }

    private sealed class GeneratedMeshItem
    {
        public long SourceId;
        public List<long> NodeIds;
        public Dictionary<string, string> Tags;
        public Mesh Mesh;
        public Vector2 CenterXZ;
    }

    private sealed class GeneratedPointItem
    {
        public long SourceId;
        public Vector3 Position;
        public PointKind Kind;
        public Dictionary<string, string> Tags;
    }

    private struct GeoBounds
    {
        public double South;
        public double West;
        public double North;
        public double East;

        public bool IsValid => South <= North && West <= East;
    }

    [Header("Area")]
    [SerializeField] private AreaMode areaMode = AreaMode.CircleRadius;
    [SerializeField] private double centerLatitude = 0.0;
    [SerializeField] private double centerLongitude = 0.0;
    [SerializeField] private float searchRadius = 500f;
    [SerializeField] private double minLatitude = 0.0;
    [SerializeField] private double minLongitude = 0.0;
    [SerializeField] private double maxLatitude = 0.0;
    [SerializeField] private double maxLongitude = 0.0;

    [Header("Feature Toggles")]
    [SerializeField] private bool includeBuildings = true;
    [SerializeField] private bool includeRoads = true;
    [SerializeField] private bool includeWaterAreas = true;
    [SerializeField] private bool includePointMarkers = true;

    [Header("Building Settings")]
    [SerializeField] private float defaultBuildingHeight = 3.0f;
    [SerializeField] private bool combineBuildingsIntoSingleMesh = false;
    [SerializeField] private float chunkSizeMeters = 250f;

    [Header("Road Settings")]
    [SerializeField] private bool combineRoadsIntoSingleMesh = false;
    [SerializeField] private float roadHeightOffset = 0.05f;
    [SerializeField] private float roadWidthScale = 1.0f;

    [Header("Water Settings")]
    [SerializeField] private bool combineWaterIntoSingleMesh = false;
    [SerializeField] private float waterHeightOffset = 0.02f;

    [Header("Point Marker Settings")]
    [SerializeField] private float pointMarkerScale = 1.0f;

    [Header("Base Materials")]
    [SerializeField] private Material facadeMaterial;
    [SerializeField] private Material roofMaterial;
    [SerializeField] private Material roadMaterial;
    [SerializeField] private Material waterMaterial;
    [SerializeField] private Material pointMaterial;

    [Header("Output")]
    [SerializeField] private bool saveMeshesAsAssets = false;
    [SerializeField] private bool saveMaterialsAsAssets = true;
    [SerializeField] private string assetFolderPath = DefaultAssetFolder;

    [Header("Per-Building Texture Overrides")]
    [SerializeField] private bool showTextureOverrides = true;
    [SerializeField] private long overrideBuildingId = 0;
    [SerializeField] private Texture2D overrideFacadeTexture;
    [SerializeField] private Texture2D overrideRoofTexture;
    [SerializeField] private List<TextureOverrideEntry> textureOverrides = new List<TextureOverrideEntry>();

    private bool isGenerating;

    [MenuItem("Tools/OSM City Builder")]
    public static void Open()
    {
        GetWindow<OSMCityBuilderWindow>("OSM City Builder");
    }

    private void OnEnable()
    {
        if (textureOverrides == null)
            textureOverrides = new List<TextureOverrideEntry>();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("OSM City Builder", EditorStyles.boldLabel);
        EditorGUILayout.Space(6);

        EditorGUILayout.HelpBox(
            "Импортирует данные OpenStreetMap (Overpass API) и строит в сцене здания, дороги, водные объекты и простые точечные маркеры.",
            MessageType.Info);

        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField("Area", EditorStyles.boldLabel);
        areaMode = (AreaMode)EditorGUILayout.EnumPopup("Area Mode", areaMode);

        if (areaMode == AreaMode.CircleRadius)
        {
            centerLatitude = EditorGUILayout.DoubleField("Center Latitude", centerLatitude);
            centerLongitude = EditorGUILayout.DoubleField("Center Longitude", centerLongitude);
            searchRadius = EditorGUILayout.FloatField("Search Radius (m)", searchRadius);
            searchRadius = Mathf.Max(1f, searchRadius);
            EditorGUILayout.HelpBox(
                "Круговая область удобна для быстрого импорта. Для точного прямоугольника переключись в Bounding Box.",
                MessageType.Info);
        }
        else
        {
            minLatitude = EditorGUILayout.DoubleField("Min Latitude", minLatitude);
            minLongitude = EditorGUILayout.DoubleField("Min Longitude", minLongitude);
            maxLatitude = EditorGUILayout.DoubleField("Max Latitude", maxLatitude);
            maxLongitude = EditorGUILayout.DoubleField("Max Longitude", maxLongitude);
        }

        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField("Feature Toggles", EditorStyles.boldLabel);
        includeBuildings = EditorGUILayout.ToggleLeft("Buildings", includeBuildings);
        includeRoads = EditorGUILayout.ToggleLeft("Roads", includeRoads);
        includeWaterAreas = EditorGUILayout.ToggleLeft("Water Areas", includeWaterAreas);
        includePointMarkers = EditorGUILayout.ToggleLeft("Point Markers", includePointMarkers);

        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField("Building Settings", EditorStyles.boldLabel);
        defaultBuildingHeight = EditorGUILayout.FloatField("Default Building Height", defaultBuildingHeight);
        combineBuildingsIntoSingleMesh = EditorGUILayout.ToggleLeft("Combine Buildings Into Single Mesh", combineBuildingsIntoSingleMesh);

        using (new EditorGUI.DisabledScope(combineBuildingsIntoSingleMesh))
        {
            chunkSizeMeters = EditorGUILayout.FloatField("Chunk Size (m)", chunkSizeMeters);
            chunkSizeMeters = Mathf.Max(1f, chunkSizeMeters);
        }

        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField("Road Settings", EditorStyles.boldLabel);
        combineRoadsIntoSingleMesh = EditorGUILayout.ToggleLeft("Combine Roads Into Single Mesh", combineRoadsIntoSingleMesh);
        roadHeightOffset = EditorGUILayout.FloatField("Road Height Offset", roadHeightOffset);
        roadWidthScale = EditorGUILayout.FloatField("Road Width Scale", roadWidthScale);
        roadWidthScale = Mathf.Max(0.1f, roadWidthScale);

        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField("Water Settings", EditorStyles.boldLabel);
        combineWaterIntoSingleMesh = EditorGUILayout.ToggleLeft("Combine Water Areas Into Single Mesh", combineWaterIntoSingleMesh);
        waterHeightOffset = EditorGUILayout.FloatField("Water Height Offset", waterHeightOffset);

        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField("Point Marker Settings", EditorStyles.boldLabel);
        pointMarkerScale = EditorGUILayout.FloatField("Point Marker Scale", pointMarkerScale);
        pointMarkerScale = Mathf.Max(0.1f, pointMarkerScale);

        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField("Base Materials", EditorStyles.boldLabel);
        facadeMaterial = (Material)EditorGUILayout.ObjectField("Facade Material", facadeMaterial, typeof(Material), false);
        roofMaterial = (Material)EditorGUILayout.ObjectField("Roof Material", roofMaterial, typeof(Material), false);
        roadMaterial = (Material)EditorGUILayout.ObjectField("Road Material", roadMaterial, typeof(Material), false);
        waterMaterial = (Material)EditorGUILayout.ObjectField("Water Material", waterMaterial, typeof(Material), false);
        pointMaterial = (Material)EditorGUILayout.ObjectField("Point Material", pointMaterial, typeof(Material), false);

        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        saveMeshesAsAssets = EditorGUILayout.ToggleLeft("Save Meshes as .asset files", saveMeshesAsAssets);
        saveMaterialsAsAssets = EditorGUILayout.ToggleLeft("Save Materials as .asset files", saveMaterialsAsAssets);

        using (new EditorGUI.DisabledScope(!saveMeshesAsAssets && !saveMaterialsAsAssets))
        {
            assetFolderPath = EditorGUILayout.TextField("Asset Folder", assetFolderPath);
        }

        EditorGUILayout.Space(8);

        showTextureOverrides = EditorGUILayout.Foldout(showTextureOverrides, "Per-Building Texture Overrides", true);
        if (showTextureOverrides)
        {
            EditorGUI.indentLevel++;

            if (combineBuildingsIntoSingleMesh)
            {
                EditorGUILayout.HelpBox(
                    "Texture overrides for individual buildings are ignored when all buildings are merged into one mesh.",
                    MessageType.Warning);
            }

            overrideBuildingId = EditorGUILayout.LongField("OSM Building Id", overrideBuildingId);
            overrideFacadeTexture = (Texture2D)EditorGUILayout.ObjectField("Facade Texture", overrideFacadeTexture, typeof(Texture2D), false);
            overrideRoofTexture = (Texture2D)EditorGUILayout.ObjectField("Roof Texture", overrideRoofTexture, typeof(Texture2D), false);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add / Update Override"))
            {
                AddOrUpdateTextureOverride();
            }

            if (GUILayout.Button("Clear Fields"))
            {
                overrideBuildingId = 0;
                overrideFacadeTexture = null;
                overrideRoofTexture = null;
            }

            if (GUILayout.Button("Remove All"))
            {
                textureOverrides.Clear();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            if (textureOverrides.Count == 0)
            {
                EditorGUILayout.LabelField("No overrides yet.");
            }
            else
            {
                for (int i = 0; i < textureOverrides.Count; i++)
                {
                    TextureOverrideEntry entry = textureOverrides[i];
                    if (entry == null)
                        continue;

                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                    EditorGUILayout.LabelField($"OSM ID: {entry.OsmId}");
                    EditorGUILayout.ObjectField("Facade Texture", entry.FacadeTexture, typeof(Texture2D), false);
                    EditorGUILayout.ObjectField("Roof Texture", entry.RoofTexture, typeof(Texture2D), false);

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Remove", GUILayout.Width(80)))
                    {
                        textureOverrides.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(10);

        EditorGUI.BeginDisabledGroup(isGenerating);
        if (GUILayout.Button("Generate Map Area", GUILayout.Height(36)))
        {
            StartGeneration();
        }
        EditorGUI.EndDisabledGroup();

        if (isGenerating)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox("Generating... Editor UI remains responsive.", MessageType.None);
        }
    }

    private void StartGeneration()
    {
        if (isGenerating)
            return;

        SimpleEditorCoroutineRunner.Start(GenerateCoroutine());
    }

    private void AddOrUpdateTextureOverride()
    {
        if (overrideBuildingId <= 0)
        {
            Debug.LogError("OSM City Builder: OSM Building Id must be greater than zero.");
            return;
        }

        TextureOverrideEntry existing = textureOverrides.Find(x => x != null && x.OsmId == overrideBuildingId);
        if (existing == null)
        {
            textureOverrides.Add(new TextureOverrideEntry
            {
                OsmId = overrideBuildingId,
                FacadeTexture = overrideFacadeTexture,
                RoofTexture = overrideRoofTexture
            });
        }
        else
        {
            existing.FacadeTexture = overrideFacadeTexture;
            existing.RoofTexture = overrideRoofTexture;
        }

        Repaint();
    }

    private IEnumerator GenerateCoroutine()
    {
        if (isGenerating)
            yield break;

        if (!ValidateInput(out GeoBounds bounds))
            yield break;

        isGenerating = true;
        EditorUtility.DisplayProgressBar("OSM City Builder", "Preparing Overpass query...", 0f);

        try
        {
            string query = BuildOverpassQuery(bounds);
            EditorUtility.DisplayProgressBar("OSM City Builder", "Downloading OpenStreetMap data...", 0.08f);

            string json;
            using (UnityWebRequest request = CreateOverpassRequest(query))
            {
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                while (!op.isDone)
                    yield return null;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"OSM City Builder: Overpass request failed. HTTP {(long)request.responseCode}: {request.error}");
                    yield break;
                }

                json = request.downloadHandler.text;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                Debug.LogError("OSM City Builder: Overpass returned an empty response.");
                yield break;
            }

            EditorUtility.DisplayProgressBar("OSM City Builder", "Parsing JSON...", 0.16f);
            OverpassData data = OverpassJsonParser.Parse(json);
            if (data == null)
            {
                Debug.LogError("OSM City Builder: Failed to parse Overpass JSON.");
                yield break;
            }

            if (data.Nodes.Count == 0)
            {
                Debug.LogError("OSM City Builder: No nodes returned by Overpass.");
                yield break;
            }

            GameObject root = EnsureRootContainer();
            MaterialPair buildingBase = ResolveBuildingMaterials();
            if (buildingBase == null || buildingBase.Facade == null || buildingBase.Roof == null)
            {
                Debug.LogError("OSM City Builder: No valid building materials available.");
                yield break;
            }

            Material roadMat = ResolveSingleMaterial(roadMaterial, "OSM_Road_Material");
            Material waterMat = ResolveSingleMaterial(waterMaterial, "OSM_Water_Material");
            Material pointMat = ResolveSingleMaterial(pointMaterial, "OSM_Point_Material");

            if (saveMaterialsAsAssets && (roadMat != null || waterMat != null || pointMat != null))
                savedAnyMaterials = true;

            Dictionary<long, OSMNode> nodeLookup = data.BuildNodeLookup();

            List<GeneratedBuildingItem> buildings = includeBuildings ? ExtractBuildings(data) : new List<GeneratedBuildingItem>();
            List<GeneratedMeshItem> roads = includeRoads ? ExtractRoadWays(data) : new List<GeneratedMeshItem>();
            List<GeneratedMeshItem> waterAreas = includeWaterAreas ? ExtractWaterAreas(data) : new List<GeneratedMeshItem>();
            List<GeneratedPointItem> points = includePointMarkers ? ExtractPointMarkers(data, nodeLookup, bounds) : new List<GeneratedPointItem>();

            if (buildings.Count == 0 && roads.Count == 0 && waterAreas.Count == 0 && points.Count == 0)
            {
                Debug.LogWarning("OSM City Builder: No supported features were found in the selected area.");
                yield break;
            }

            bool savedAnyMeshes = false;
            bool savedAnyMaterials = false;

            if (includeBuildings && buildings.Count > 0)
            {
                EditorUtility.DisplayProgressBar("OSM City Builder", "Preparing buildings...", 0.25f);
                int createdBuildingObjects = CreateBuildingObjects(
                    root.transform,
                    buildingBase,
                    buildings,
                    nodeLookup,
                    areaCenter,
                    out bool buildingMeshesSaved,
                    out bool buildingMaterialsSaved);
                savedAnyMeshes |= buildingMeshesSaved;
                savedAnyMaterials |= buildingMaterialsSaved;
                Debug.Log($"OSM City Builder: Created {createdBuildingObjects} building objects.");
                yield return null;
            }

            if (includeRoads && roads.Count > 0)
            {
                EditorUtility.DisplayProgressBar("OSM City Builder", "Preparing roads...", 0.60f);
                int createdRoadObjects = CreateSingleMaterialMeshObjects(
                    root.transform,
                    "OSM_Road",
                    roads,
                    nodeLookup,
                    areaCenter,
                    roadMat,
                    combineRoadsIntoSingleMesh,
                    roadHeightOffset,
                    out bool roadMeshesSaved);
                savedAnyMeshes |= roadMeshesSaved;
                Debug.Log($"OSM City Builder: Created {createdRoadObjects} road objects.");
                yield return null;
            }

            if (includeWaterAreas && waterAreas.Count > 0)
            {
                EditorUtility.DisplayProgressBar("OSM City Builder", "Preparing water areas...", 0.75f);
                int createdWaterObjects = CreateSingleMaterialMeshObjects(
                    root.transform,
                    "OSM_Water",
                    waterAreas,
                    nodeLookup,
                    areaCenter,
                    waterMat,
                    combineWaterIntoSingleMesh,
                    waterHeightOffset,
                    out bool waterMeshesSaved);
                savedAnyMeshes |= waterMeshesSaved;
                Debug.Log($"OSM City Builder: Created {createdWaterObjects} water objects.");
                yield return null;
            }

            if (includePointMarkers && points.Count > 0)
            {
                EditorUtility.DisplayProgressBar("OSM City Builder", "Creating point markers...", 0.88f);
                int createdPoints = CreatePointMarkers(root.transform, points, pointMat);
                Debug.Log($"OSM City Builder: Created {createdPoints} point markers.");
                yield return null;
            }

            if (saveMeshesAsAssets || saveMaterialsAsAssets)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            if (savedAnyMeshes)
                Debug.Log("OSM City Builder: Mesh assets were saved to disk.");

            if (savedAnyMaterials)
                Debug.Log("OSM City Builder: Material assets were saved to disk.");

            Selection.activeGameObject = root;
            Debug.Log($"OSM City Builder: Generation finished. Root object: '{RootName}'.");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            isGenerating = false;
            Repaint();
        }
    }

    private bool ValidateInput(out GeoBounds bounds)
    {
        bounds = default;

        if (!includeBuildings && !includeRoads && !includeWaterAreas && !includePointMarkers)
        {
            Debug.LogError("OSM City Builder: At least one feature type must be enabled.");
            return false;
        }

        if (searchRadius <= 0f)
        {
            if (areaMode == AreaMode.CircleRadius)
            {
                Debug.LogError("OSM City Builder: Search Radius must be greater than 0.");
                return false;
            }
        }

        if (areaMode == AreaMode.CircleRadius)
        {
            if (centerLatitude < -90.0 || centerLatitude > 90.0 || centerLongitude < -180.0 || centerLongitude > 180.0)
            {
                Debug.LogError("OSM City Builder: Invalid center latitude/longitude.");
                return false;
            }

            if (searchRadius <= 0f)
            {
                Debug.LogError("OSM City Builder: Search Radius must be greater than 0.");
                return false;
            }

            bounds = BoundsFromCircle(centerLatitude, centerLongitude, searchRadius);
            return true;
        }

        if (minLatitude < -90.0 || maxLatitude > 90.0 || minLongitude < -180.0 || maxLongitude > 180.0)
        {
            Debug.LogError("OSM City Builder: Bounding box values are out of range.");
            return false;
        }

        bounds = new GeoBounds
        {
            South = Math.Min(minLatitude, maxLatitude),
            North = Math.Max(minLatitude, maxLatitude),
            West = Math.Min(minLongitude, maxLongitude),
            East = Math.Max(minLongitude, maxLongitude)
        };

        if (!bounds.IsValid)
        {
            Debug.LogError("OSM City Builder: Invalid bounding box.");
            return false;
        }

        if (Math.Abs(bounds.North - bounds.South) < 0.000001 || Math.Abs(bounds.East - bounds.West) < 0.000001)
        {
            Debug.LogError("OSM City Builder: Bounding box is too small.");
            return false;
        }

        return true;
    }

    private static GeoBounds BoundsFromCircle(double lat, double lon, float radiusMeters)
    {
        double latDelta = radiusMeters / 111320.0;
        double cosLat = Math.Cos(lat * Math.PI / 180.0);
        double lonScale = Math.Max(0.0001, Math.Abs(cosLat));
        double lonDelta = radiusMeters / (111320.0 * lonScale);

        return new GeoBounds
        {
            South = lat - latDelta,
            North = lat + latDelta,
            West = lon - lonDelta,
            East = lon + lonDelta
        };
    }

    private static string BuildOverpassQuery(GeoBounds bounds)
    {
        string south = bounds.South.ToString("0.######", CultureInfo.InvariantCulture);
        string west = bounds.West.ToString("0.######", CultureInfo.InvariantCulture);
        string north = bounds.North.ToString("0.######", CultureInfo.InvariantCulture);
        string east = bounds.East.ToString("0.######", CultureInfo.InvariantCulture);
        string bbox = $"({south},{west},{north},{east})";

        var sb = new StringBuilder();
        sb.Append("[out:json][timeout:180];(");

        sb.Append($"way[\"building\"]{bbox};");
        sb.Append($"relation[\"building\"]{bbox};");

        sb.Append($"way[\"highway\"]{bbox};");

        sb.Append($"way[\"natural\"=\"water\"]{bbox};");
        sb.Append($"way[\"waterway\"=\"riverbank\"]{bbox};");
        sb.Append($"way[\"landuse\"=\"reservoir\"]{bbox};");
        sb.Append($"relation[\"natural\"=\"water\"]{bbox};");
        sb.Append($"relation[\"waterway\"=\"riverbank\"]{bbox};");
        sb.Append($"relation[\"landuse\"=\"reservoir\"]{bbox};");

        sb.Append($"node[\"amenity\"]{bbox};");
        sb.Append($"node[\"man_made\"]{bbox};");
        sb.Append($"node[\"natural\"=\"tree\"]{bbox};");
        sb.Append($"node[\"tourism\"]{bbox};");
        sb.Append($"node[\"leisure\"]{bbox};");
        sb.Append($"node[\"historic\"]{bbox};");
        sb.Append($"node[\"shop\"]{bbox};");

        sb.Append(");out body;>;out skel qt;");
        return sb.ToString();
    }

    private static UnityWebRequest CreateOverpassRequest(string query)
    {
        WWWForm form = new WWWForm();
        form.AddField("data", query);

        UnityWebRequest request = UnityWebRequest.Post(OverpassUrl, form);
        request.timeout = 180;
        request.SetRequestHeader("Accept", "application/json");
        request.SetRequestHeader("User-Agent", "OSMCityBuilder/2.0 (Unity Editor)");
        return request;
    }

    private GameObject EnsureRootContainer()
    {
        GameObject root = GameObject.Find(RootName);
        if (root == null)
        {
            root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create OSM City Root");
        }
        else
        {
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = root.transform.GetChild(i).gameObject;
                Undo.DestroyObjectImmediate(child);
            }
        }

        root.transform.position = Vector3.zero;
        return root;
    }

    private MaterialPair ResolveBuildingMaterials()
    {
        Material facade = ResolveSingleMaterial(facadeMaterial, "OSM_Building_Facade");
        if (facade == null)
            facade = CreateFallbackMaterial("OSM_Building_Facade");

        if (facade == null)
            return null;

        Material roof = ResolveSingleMaterial(roofMaterial, "OSM_Building_Roof");
        if (roof == null)
            roof = facade;

        return new MaterialPair
        {
            Facade = facade,
            Roof = roof
        };
    }

    private Material ResolveSingleMaterial(Material source, string assetPrefix)
    {
        Material mat = source != null ? source : CreateFallbackMaterial(assetPrefix);
        if (mat == null)
            return null;

        if (!saveMaterialsAsAssets)
            return mat;

        EnsureAssetFolders(assetFolderPath);
        Material clone = CloneMaterial(mat, assetPrefix);
        clone = SaveMaterialAsset(clone, $"{assetFolderPath}/Materials", assetPrefix);
        return clone;
    }

    private static Material CreateFallbackMaterial(string name)
    {
        Shader shader = FindBestAvailableShader();
        if (shader == null)
            return null;

        return new Material(shader)
        {
            name = name
        };
    }

    private static Shader FindBestAvailableShader()
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("HDRP/Lit");
        if (shader == null) shader = Shader.Find("Unlit/Texture");
        if (shader == null) shader = Shader.Find("Sprites/Default");
        return shader;
    }

    private MaterialPair CreateMaterialPair(
        MaterialPair basePair,
        string assetPrefix,
        TextureOverrideEntry overrideEntry,
        bool ignoreOverride,
        out bool savedAsAsset)
    {
        savedAsAsset = false;
        if (basePair == null || basePair.Facade == null || basePair.Roof == null)
            return null;

        bool useOverride = overrideEntry != null && !ignoreOverride;
        bool createUnique = saveMaterialsAsAssets || useOverride;

        Material facade = createUnique ? CloneMaterial(basePair.Facade, $"{assetPrefix}_Facade") : basePair.Facade;
        Material roof = createUnique ? CloneMaterial(basePair.Roof, $"{assetPrefix}_Roof") : basePair.Roof;

        if (facade == null || roof == null)
            return null;

        if (useOverride)
        {
            if (overrideEntry.FacadeTexture != null)
                facade.mainTexture = overrideEntry.FacadeTexture;

            if (overrideEntry.RoofTexture != null)
                roof.mainTexture = overrideEntry.RoofTexture;
            else if (overrideEntry.FacadeTexture != null)
                roof.mainTexture = overrideEntry.FacadeTexture;
        }

        if (createUnique && saveMaterialsAsAssets)
        {
            EnsureAssetFolders(assetFolderPath);
            facade = SaveMaterialAsset(facade, $"{assetFolderPath}/Materials", $"{assetPrefix}_Facade");
            roof = SaveMaterialAsset(roof, $"{assetFolderPath}/Materials", $"{assetPrefix}_Roof");
            savedAsAsset = true;
        }

        return new MaterialPair
        {
            Facade = facade,
            Roof = roof
        };
    }

    private static Material CloneMaterial(Material source, string name)
    {
        if (source == null)
        {
            Shader shader = FindBestAvailableShader();
            if (shader == null)
                return null;

            return new Material(shader) { name = name };
        }

        return new Material(source)
        {
            name = name
        };
    }

    private static List<GeneratedBuildingItem> ExtractBuildings(OverpassData data)
    {
        var result = new List<GeneratedBuildingItem>();
        var relationMemberWayIds = new HashSet<long>();

        foreach (OSMRelation relation in data.Relations.Values)
        {
            if (!HasBuildingTag(relation.Tags))
                continue;

            List<List<long>> rings = BuildRingsFromRelation(relation, data.Ways, relationMemberWayIds);
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i].Count >= 4)
                {
                    result.Add(new GeneratedBuildingItem
                    {
                        SourceId = relation.Id,
                        Tags = relation.Tags,
                        NodeIds = rings[i],
                        HasTextureOverride = HasTextureOverride(relation.Id)
                    });
                }
            }
        }

        foreach (OSMWay way in data.Ways.Values)
        {
            if (relationMemberWayIds.Contains(way.Id))
                continue;

            if (!HasBuildingTag(way.Tags))
                continue;

            List<long> nodeIds = NormalizeRingIds(way.NodeIds);
            if (nodeIds.Count >= 4)
            {
                result.Add(new GeneratedBuildingItem
                {
                    SourceId = way.Id,
                    Tags = way.Tags,
                    NodeIds = nodeIds,
                    HasTextureOverride = HasTextureOverride(way.Id)
                });
            }
        }

        return result;
    }

    private static List<GeneratedMeshItem> ExtractRoadWays(OverpassData data)
    {
        var result = new List<GeneratedMeshItem>();

        foreach (OSMWay way in data.Ways.Values)
        {
            if (!HasHighwayTag(way.Tags))
                continue;

            List<long> nodeIds = NormalizeWayIds(way.NodeIds);
            if (nodeIds.Count < 2)
                continue;

            result.Add(new GeneratedMeshItem
            {
                SourceId = way.Id,
                NodeIds = nodeIds,
                Tags = way.Tags,
                Mesh = null,
                CenterXZ = Vector2.zero
            });
        }

        return result;
    }

    private static List<GeneratedMeshItem> ExtractWaterAreas(OverpassData data)
    {
        var result = new List<GeneratedMeshItem>();
        var relationMemberWayIds = new HashSet<long>();

        foreach (OSMRelation relation in data.Relations.Values)
        {
            if (!HasWaterAreaTag(relation.Tags))
                continue;

            List<List<long>> rings = BuildRingsFromRelation(relation, data.Ways, relationMemberWayIds);
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i].Count >= 4)
                {
                    result.Add(new GeneratedMeshItem
                    {
                        SourceId = relation.Id,
                        NodeIds = rings[i],
                        Tags = relation.Tags,
                        Mesh = null,
                        CenterXZ = Vector2.zero
                    });
                }
            }
        }

        foreach (OSMWay way in data.Ways.Values)
        {
            if (relationMemberWayIds.Contains(way.Id))
                continue;

            if (!HasWaterAreaTag(way.Tags))
                continue;

            List<long> nodeIds = NormalizeRingIds(way.NodeIds);
            if (nodeIds.Count >= 4)
            {
                result.Add(new GeneratedMeshItem
                {
                    SourceId = way.Id,
                    NodeIds = nodeIds,
                    Tags = way.Tags,
                    Mesh = null,
                    CenterXZ = Vector2.zero
                });
            }
        }

        return result;
    }

    private static List<GeneratedPointItem> ExtractPointMarkers(OverpassData data, Dictionary<long, OSMNode> nodeLookup, GeoBounds bounds)
    {
        var result = new List<GeneratedPointItem>();

        foreach (OSMNode node in data.Nodes.Values)
        {
            if (!TryResolvePointKind(nodeLookup, node, out PointKind kind))
                continue;

            Vector3 local = LatLonToLocalMercator(node.Lat, node.Lon, BoundsCenterLat(bounds), BoundsCenterLon(bounds));
            result.Add(new GeneratedPointItem
            {
                SourceId = node.Id,
                Position = new Vector3(local.x, 0f, local.z),
                Kind = kind,
                Tags = null
            });
        }

        return result;
    }

    private static double BoundsCenterLat(GeoBounds bounds)
    {
        return (bounds.South + bounds.North) * 0.5;
    }

    private static double BoundsCenterLon(GeoBounds bounds)
    {
        return (bounds.West + bounds.East) * 0.5;
    }

    private static bool HasBuildingTag(Dictionary<string, string> tags)
    {
        if (tags == null)
            return false;

        if (!tags.TryGetValue("building", out string buildingValue))
            return false;

        if (string.IsNullOrWhiteSpace(buildingValue))
            return false;

        string v = buildingValue.Trim().ToLowerInvariant();
        return v != "no" && v != "0" && v != "false";
    }

    private static bool HasHighwayTag(Dictionary<string, string> tags)
    {
        if (tags == null)
            return false;

        if (!tags.TryGetValue("highway", out string highwayValue))
            return false;

        if (string.IsNullOrWhiteSpace(highwayValue))
            return false;

        string v = highwayValue.Trim().ToLowerInvariant();
        return v != "no" && v != "0" && v != "false" && v != "construction" && v != "proposed" && v != "abandoned";
    }

    private static bool HasWaterAreaTag(Dictionary<string, string> tags)
    {
        if (tags == null)
            return false;

        if (tags.TryGetValue("natural", out string natural) && natural != null && natural.Equals("water", StringComparison.OrdinalIgnoreCase))
            return true;

        if (tags.TryGetValue("waterway", out string waterway) && waterway != null && waterway.Equals("riverbank", StringComparison.OrdinalIgnoreCase))
            return true;

        if (tags.TryGetValue("landuse", out string landuse) && landuse != null)
        {
            string v = landuse.Trim().ToLowerInvariant();
            if (v == "reservoir" || v == "basin" || v == "pond")
                return true;
        }

        return false;
    }

    private static bool TryResolvePointKind(Dictionary<string, string> tags, out PointKind kind)
    {
        kind = PointKind.Generic;
        if (tags == null || tags.Count == 0)
            return false;

        string value;
        if (tags.TryGetValue("natural", out value) && value != null)
        {
            string v = value.Trim().ToLowerInvariant();
            if (v == "tree")
            {
                kind = PointKind.Tree;
                return true;
            }
        }

        if (tags.TryGetValue("amenity", out value) && value != null)
        {
            string v = value.Trim().ToLowerInvariant();
            if (v == "bench")
            {
                kind = PointKind.Bench;
                return true;
            }
            if (v == "fountain")
            {
                kind = PointKind.Fountain;
                return true;
            }
            if (v == "waste_basket" || v == "post_box" || v == "drinking_water")
            {
                kind = PointKind.Generic;
                return true;
            }
        }

        if (tags.TryGetValue("man_made", out value) && value != null)
        {
            string v = value.Trim().ToLowerInvariant();
            if (v == "water_well" || v == "well")
            {
                kind = PointKind.Well;
                return true;
            }
            if (v == "lamp_post")
            {
                kind = PointKind.Lamp;
                return true;
            }
            if (v == "tower" || v == "chimney" || v == "cross")
            {
                kind = PointKind.Monument;
                return true;
            }
        }

        if (tags.TryGetValue("highway", out value) && value != null)
        {
            string v = value.Trim().ToLowerInvariant();
            if (v == "street_lamp")
            {
                kind = PointKind.Lamp;
                return true;
            }
            if (v == "traffic_signals" || v == "crossing")
            {
                kind = PointKind.Generic;
                return true;
            }
        }

        if (tags.TryGetValue("tourism", out value) && value != null)
        {
            kind = PointKind.Info;
            return true;
        }

        if (tags.TryGetValue("historic", out value) && value != null)
        {
            kind = PointKind.Monument;
            return true;
        }

        if (tags.TryGetValue("leisure", out value) && value != null)
        {
            string v = value.Trim().ToLowerInvariant();
            if (v == "playground")
            {
                kind = PointKind.Generic;
                return true;
            }
        }

        return false;
    }

    private static List<List<long>> BuildRingsFromRelation(
        OSMRelation relation,
        Dictionary<long, OSMWay> ways,
        HashSet<long> relationMemberWayIds)
    {
        var outerSegments = new List<List<long>>();

        for (int i = 0; i < relation.Members.Count; i++)
        {
            OSMRelationMember m = relation.Members[i];
            if (m.Type != "way")
                continue;

            if (!string.IsNullOrEmpty(m.Role) && !m.Role.Equals("outer", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ways.TryGetValue(m.RefId, out OSMWay way))
                continue;

            relationMemberWayIds.Add(way.Id);
            outerSegments.Add(NormalizeRingIds(way.NodeIds));
        }

        return StitchSegmentsToClosedRings(outerSegments);
    }

    private static List<List<long>> StitchSegmentsToClosedRings(List<List<long>> segments)
    {
        var unused = new List<List<long>>();
        for (int i = 0; i < segments.Count; i++)
        {
            List<long> s = NormalizeRingIds(segments[i]);
            if (s.Count >= 2)
                unused.Add(s);
        }

        var rings = new List<List<long>>();

        while (unused.Count > 0)
        {
            List<long> ring = new List<long>(unused[0]);
            unused.RemoveAt(0);

            bool progress = true;
            while (progress)
            {
                progress = false;

                for (int i = 0; i < unused.Count; i++)
                {
                    if (TryMergeSegment(ref ring, unused[i]))
                    {
                        unused.RemoveAt(i);
                        progress = true;
                        break;
                    }
                }
            }

            if (ring.Count >= 4)
            {
                if (ring[0] != ring[ring.Count - 1])
                    ring.Add(ring[0]);

                if (ring.Count >= 4 && ring[0] == ring[ring.Count - 1])
                    rings.Add(ring);
            }
        }

        return rings;
    }

    private static bool TryMergeSegment(ref List<long> ring, List<long> segment)
    {
        List<long> seg = NormalizeRingIds(segment);
        if (seg.Count < 2)
            return false;

        long ringStart = ring[0];
        long ringEnd = ring[ring.Count - 1];
        long segStart = seg[0];
        long segEnd = seg[seg.Count - 1];

        if (ringEnd == segStart)
        {
            for (int i = 1; i < seg.Count; i++) ring.Add(seg[i]);
            return true;
        }

        if (ringEnd == segEnd)
        {
            for (int i = seg.Count - 2; i >= 0; i--) ring.Add(seg[i]);
            return true;
        }

        if (ringStart == segEnd)
        {
            var newRing = new List<long>(seg.Count + ring.Count - 1);
            for (int i = 0; i < seg.Count - 1; i++) newRing.Add(seg[i]);
            for (int i = 0; i < ring.Count; i++) newRing.Add(ring[i]);
            ring = newRing;
            return true;
        }

        if (ringStart == segStart)
        {
            var newRing = new List<long>(seg.Count + ring.Count - 1);
            for (int i = seg.Count - 1; i >= 1; i--) newRing.Add(seg[i]);
            for (int i = 0; i < ring.Count; i++) newRing.Add(ring[i]);
            ring = newRing;
            return true;
        }

        return false;
    }

    private static List<long> NormalizeRingIds(List<long> ids)
    {
        var result = new List<long>();
        if (ids == null)
            return result;

        int count = ids.Count;
        if (count > 1 && ids[0] == ids[count - 1])
            count--;

        long prev = long.MinValue;
        for (int i = 0; i < count; i++)
        {
            long id = ids[i];
            if (id == prev)
                continue;

            result.Add(id);
            prev = id;
        }

        return result;
    }

    private static List<long> NormalizeWayIds(List<long> ids)
    {
        var result = new List<long>();
        if (ids == null)
            return result;

        long prev = long.MinValue;
        for (int i = 0; i < ids.Count; i++)
        {
            long id = ids[i];
            if (id == prev)
                continue;

            result.Add(id);
            prev = id;
        }

        return result;
    }

    private static bool TryBuildFootprint(
        List<long> nodeIds,
        Dictionary<long, OSMNode> nodeLookup,
        double centerLat,
        double centerLon,
        out List<Vector2> footprint)
    {
        footprint = new List<Vector2>();
        if (nodeIds == null || nodeIds.Count < 3)
            return false;

        int count = nodeIds.Count;
        if (nodeIds[0] == nodeIds[count - 1])
            count--;

        for (int i = 0; i < count; i++)
        {
            long nodeId = nodeIds[i];
            if (!nodeLookup.TryGetValue(nodeId, out OSMNode node))
                return false;

            Vector3 local = LatLonToLocalMercator(node.Lat, node.Lon, centerLat, centerLon);
            footprint.Add(new Vector2(local.x, local.z));
        }

        footprint = CleanupPolygon(footprint);
        if (footprint.Count < 3)
            return false;

        float area = SignedArea(footprint);
        if (Mathf.Abs(area) < 0.001f)
            return false;

        if (area < 0f)
            footprint.Reverse();

        return true;
    }

    private static bool TryBuildPolyline(
        List<long> nodeIds,
        Dictionary<long, OSMNode> nodeLookup,
        double centerLat,
        double centerLon,
        out List<Vector2> points)
    {
        points = new List<Vector2>();
        if (nodeIds == null || nodeIds.Count < 2)
            return false;

        int count = nodeIds.Count;
        for (int i = 0; i < count; i++)
        {
            long nodeId = nodeIds[i];
            if (!nodeLookup.TryGetValue(nodeId, out OSMNode node))
                return false;

            Vector3 local = LatLonToLocalMercator(node.Lat, node.Lon, centerLat, centerLon);
            Vector2 p = new Vector2(local.x, local.z);

            if (points.Count == 0 || Vector2.Distance(points[points.Count - 1], p) > 0.001f)
                points.Add(p);
        }

        return points.Count >= 2;
    }

    private static List<Vector2> CleanupPolygon(List<Vector2> points)
    {
        const float duplicateEpsilon = 0.001f;
        const float collinearEpsilon = 0.0001f;

        var cleaned = new List<Vector2>();
        if (points == null || points.Count == 0)
            return cleaned;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2 p = points[i];
            if (cleaned.Count == 0 || Vector2.Distance(cleaned[cleaned.Count - 1], p) > duplicateEpsilon)
                cleaned.Add(p);
        }

        if (cleaned.Count > 1 && Vector2.Distance(cleaned[0], cleaned[cleaned.Count - 1]) <= duplicateEpsilon)
            cleaned.RemoveAt(cleaned.Count - 1);

        bool removed = true;
        while (removed && cleaned.Count >= 4)
        {
            removed = false;

            for (int i = 0; i < cleaned.Count; i++)
            {
                int prev = (i - 1 + cleaned.Count) % cleaned.Count;
                int next = (i + 1) % cleaned.Count;

                float cross = Cross(cleaned[prev], cleaned[i], cleaned[next]);
                if (Mathf.Abs(cross) <= collinearEpsilon)
                {
                    cleaned.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        return cleaned;
    }


    private int CreateBuildingObjects(
        Transform parent,
        MaterialPair basePair,
        List<GeneratedBuildingItem> buildings,
        Dictionary<long, OSMNode> nodeLookup,
        Vector2 areaCenter,
        out bool savedMeshes,
        out bool savedMaterials)
    {
        savedMeshes = false;
        savedMaterials = false;

        if (buildings == null || buildings.Count == 0)
            return 0;

        if (combineBuildingsIntoSingleMesh && textureOverrides != null && textureOverrides.Count > 0)
        {
            Debug.LogWarning("OSM City Builder: Building texture overrides are ignored when combining all buildings into a single mesh.");
        }

        int createdObjects = 0;

        if (combineBuildingsIntoSingleMesh)
        {
            var allItems = new List<GeneratedMeshItem>(buildings.Count);

            for (int i = 0; i < buildings.Count; i++)
            {
                GeneratedBuildingItem item = buildings[i];
                if (item == null)
                    continue;

                if (!TryBuildFootprint(item.NodeIds, nodeLookup, areaCenter.x, areaCenter.y, out List<Vector2> footprint))
                    continue;

                float height = ResolveBuildingHeight(item.Tags, defaultBuildingHeight);
                Mesh mesh = BuildExtrudedBuildingMesh(footprint, height);
                if (mesh == null)
                    continue;

                allItems.Add(new GeneratedMeshItem
                {
                    SourceId = item.SourceId,
                    NodeIds = item.NodeIds,
                    Tags = item.Tags,
                    Mesh = mesh,
                    CenterXZ = new Vector2(mesh.bounds.center.x, mesh.bounds.center.z)
                });

                if (i % 10 == 0)
                    EditorUtility.DisplayProgressBar("OSM City Builder", $"Preparing buildings... {i + 1}/{buildings.Count}", 0.28f);
            }

            if (allItems.Count == 0)
                return 0;

            Mesh combined = CombineMeshesPreserveSubmeshes(allItems, "OSM_Buildings_Combined");
            if (combined == null)
                return 0;

            if (saveMeshesAsAssets)
            {
                EnsureAssetFolders(assetFolderPath);
                combined = SaveMeshAsset(combined, $"{assetFolderPath}/Meshes", "OSM_Buildings_Combined");
                savedMeshes = true;
            }

            MaterialPair pair = CreateMaterialPair(basePair, "OSM_Buildings_Combined", null, true, out bool buildingMaterialsSaved);
            savedMaterials |= buildingMaterialsSaved;

            CreateGeneratedObject(parent, "OSM_Buildings_Combined", combined, pair, true);
            return 1;
        }

        var specialItems = new List<GeneratedMeshItem>();
        var regularItems = new List<GeneratedMeshItem>();

        for (int i = 0; i < buildings.Count; i++)
        {
            GeneratedBuildingItem item = buildings[i];
            if (item == null)
                continue;

            var meshItem = new GeneratedMeshItem
            {
                SourceId = item.SourceId,
                NodeIds = item.NodeIds,
                Tags = item.Tags
            };

            if (item.HasTextureOverride)
                specialItems.Add(meshItem);
            else
                regularItems.Add(meshItem);
        }

        for (int i = 0; i < specialItems.Count; i++)
        {
            GeneratedMeshItem item = specialItems[i];
            if (!TryBuildFootprint(item.NodeIds, nodeLookup, areaCenter.x, areaCenter.y, out List<Vector2> footprint))
                continue;

            float height = ResolveBuildingHeight(item.Tags, defaultBuildingHeight);
            Mesh mesh = BuildExtrudedBuildingMesh(footprint, height);
            if (mesh == null)
                continue;

            TextureOverrideEntry overrideEntry = GetTextureOverride(item.SourceId);
            MaterialPair pair = CreateMaterialPair(basePair, $"OSM_Building_{item.SourceId}", overrideEntry, false, out bool buildingMaterialsSaved);
            savedMaterials |= buildingMaterialsSaved;

            if (saveMeshesAsAssets)
            {
                EnsureAssetFolders(assetFolderPath);
                mesh = SaveMeshAsset(mesh, $"{assetFolderPath}/Meshes", $"OSM_Building_{item.SourceId}");
                savedMeshes = true;
            }

            CreateGeneratedObject(parent, $"OSM_Building_{item.SourceId}", mesh, pair, true);
            createdObjects++;

            if (i % 4 == 0)
                EditorUtility.DisplayProgressBar("OSM City Builder", $"Preparing overridden buildings... {i + 1}/{specialItems.Count}", 0.32f);
        }

        var regularMeshes = new List<GeneratedMeshItem>(regularItems.Count);
        for (int i = 0; i < regularItems.Count; i++)
        {
            GeneratedMeshItem item = regularItems[i];
            if (!TryBuildFootprint(item.NodeIds, nodeLookup, areaCenter.x, areaCenter.y, out List<Vector2> footprint))
                continue;

            float height = ResolveBuildingHeight(item.Tags, defaultBuildingHeight);
            Mesh mesh = BuildExtrudedBuildingMesh(footprint, height);
            if (mesh == null)
                continue;

            regularMeshes.Add(new GeneratedMeshItem
            {
                SourceId = item.SourceId,
                NodeIds = item.NodeIds,
                Tags = item.Tags,
                Mesh = mesh,
                CenterXZ = new Vector2(mesh.bounds.center.x, mesh.bounds.center.z)
            });
        }

        Dictionary<Vector2Int, List<GeneratedMeshItem>> chunks = GroupIntoChunks(regularMeshes, chunkSizeMeters);
        foreach (KeyValuePair<Vector2Int, List<GeneratedMeshItem>> kvp in chunks)
        {
            Vector2Int key = kvp.Key;
            List<GeneratedMeshItem> items = kvp.Value;
            if (items == null || items.Count == 0)
                continue;

            Mesh combined = CombineMeshesPreserveSubmeshes(items, $"OSM_Chunk_{key.x}_{key.y}");
            if (combined == null)
                continue;

            if (saveMeshesAsAssets)
            {
                EnsureAssetFolders(assetFolderPath);
                combined = SaveMeshAsset(combined, $"{assetFolderPath}/Meshes", $"OSM_Chunk_{key.x}_{key.y}");
                savedMeshes = true;
            }

            MaterialPair pair = CreateMaterialPair(basePair, $"OSM_Chunk_{key.x}_{key.y}", null, true, out bool buildingMaterialsSaved);
            savedMaterials |= buildingMaterialsSaved;

            CreateGeneratedObject(parent, $"OSM_Chunk_{key.x}_{key.y}", combined, pair, true);
            createdObjects++;
        }

        return createdObjects;
    }

    private int CreateSingleMaterialMeshObjects(
        Transform parent,
        string prefix,
        List<GeneratedMeshItem> items,
        Dictionary<long, OSMNode> nodeLookup,
        Vector2 areaCenter,
        Material material,
        bool combineIntoSingle,
        float heightOffset,
        out bool savedMeshes)
    {
        savedMeshes = false;
        if (items == null || items.Count == 0)
            return 0;

        bool likelyRoadCategory = prefix.IndexOf("Road", StringComparison.OrdinalIgnoreCase) >= 0;
        var builtMeshes = new List<GeneratedMeshItem>();

        for (int i = 0; i < items.Count; i++)
        {
            GeneratedMeshItem item = items[i];
            if (item == null)
                continue;

            if (likelyRoadCategory)
            {
                if (!TryBuildPolyline(item.NodeIds, nodeLookup, areaCenter.x, areaCenter.y, out List<Vector2> polyline))
                    continue;

                float width = ResolveRoadWidth(item.Tags) * roadWidthScale;
                Mesh roadMesh = BuildRoadMesh(polyline, width, heightOffset);
                if (roadMesh == null)
                    continue;

                builtMeshes.Add(new GeneratedMeshItem
                {
                    SourceId = item.SourceId,
                    NodeIds = item.NodeIds,
                    Tags = item.Tags,
                    Mesh = roadMesh,
                    CenterXZ = new Vector2(roadMesh.bounds.center.x, roadMesh.bounds.center.z)
                });
            }
            else
            {
                if (!TryBuildFootprint(item.NodeIds, nodeLookup, areaCenter.x, areaCenter.y, out List<Vector2> polygon))
                    continue;

                Mesh waterMesh = BuildFlatPolygonMesh(polygon, heightOffset);
                if (waterMesh == null)
                    continue;

                builtMeshes.Add(new GeneratedMeshItem
                {
                    SourceId = item.SourceId,
                    NodeIds = item.NodeIds,
                    Tags = item.Tags,
                    Mesh = waterMesh,
                    CenterXZ = new Vector2(waterMesh.bounds.center.x, waterMesh.bounds.center.z)
                });
            }
        }

        if (builtMeshes.Count == 0)
            return 0;

        int createdObjects = 0;

        if (combineIntoSingle)
        {
            Mesh combined = CombineMeshesSingleSubmesh(builtMeshes, $"{prefix}_Combined");
            if (combined == null)
                return 0;

            if (saveMeshesAsAssets)
            {
                EnsureAssetFolders(assetFolderPath);
                combined = SaveMeshAsset(combined, $"{assetFolderPath}/Meshes", $"{prefix}_Combined");
                savedMeshes = true;
            }

            CreateGeneratedObject(parent, $"{prefix}_Combined", combined, material, false);
            return 1;
        }

        Dictionary<Vector2Int, List<GeneratedMeshItem>> chunks = GroupIntoChunks(builtMeshes, chunkSizeMeters);
        foreach (KeyValuePair<Vector2Int, List<GeneratedMeshItem>> kvp in chunks)
        {
            Vector2Int key = kvp.Key;
            List<GeneratedMeshItem> chunkItems = kvp.Value;
            if (chunkItems == null || chunkItems.Count == 0)
                continue;

            Mesh combined = CombineMeshesSingleSubmesh(chunkItems, $"{prefix}_{key.x}_{key.y}");
            if (combined == null)
                continue;

            if (saveMeshesAsAssets)
            {
                EnsureAssetFolders(assetFolderPath);
                combined = SaveMeshAsset(combined, $"{assetFolderPath}/Meshes", $"{prefix}_{key.x}_{key.y}");
                savedMeshes = true;
            }

            CreateGeneratedObject(parent, $"{prefix}_{key.x}_{key.y}", combined, material, false);
            createdObjects++;
        }

        return createdObjects;
    }


    private int CreatePointMarkers(Transform parent, List<GeneratedPointItem> points, Material material)
    {
        if (points == null || points.Count == 0)
            return 0;

        int created = 0;
        for (int i = 0; i < points.Count; i++)
        {
            GeneratedPointItem item = points[i];
            if (item == null)
                continue;

            GameObject go = CreatePointObject(item, material);
            go.transform.SetParent(parent, false);
            created++;
        }

        return created;
    }

    private GameObject CreatePointObject(GeneratedPointItem item, Material material)
    {
        PrimitiveType primitive = PrimitiveType.Cube;
        Vector3 scale = Vector3.one * pointMarkerScale;
        float yOffset = 0f;

        switch (item.Kind)
        {
            case PointKind.Tree:
                primitive = PrimitiveType.Sphere;
                scale = Vector3.one * (pointMarkerScale * 1.8f);
                yOffset = 1.2f * pointMarkerScale;
                break;
            case PointKind.Bench:
                primitive = PrimitiveType.Cube;
                scale = new Vector3(1.5f, 0.4f, 0.4f) * pointMarkerScale;
                yOffset = 0.2f * pointMarkerScale;
                break;
            case PointKind.Fountain:
                primitive = PrimitiveType.Cylinder;
                scale = new Vector3(1.0f, 0.8f, 1.0f) * pointMarkerScale;
                yOffset = 0.4f * pointMarkerScale;
                break;
            case PointKind.Well:
                primitive = PrimitiveType.Cylinder;
                scale = new Vector3(0.9f, 1.0f, 0.9f) * pointMarkerScale;
                yOffset = 0.5f * pointMarkerScale;
                break;
            case PointKind.Lamp:
                primitive = PrimitiveType.Cylinder;
                scale = new Vector3(0.25f, 2.5f, 0.25f) * pointMarkerScale;
                yOffset = 1.25f * pointMarkerScale;
                break;
            case PointKind.Monument:
                primitive = PrimitiveType.Capsule;
                scale = new Vector3(0.8f, 1.8f, 0.8f) * pointMarkerScale;
                yOffset = 0.9f * pointMarkerScale;
                break;
            case PointKind.Info:
                primitive = PrimitiveType.Cube;
                scale = Vector3.one * (pointMarkerScale * 0.8f);
                yOffset = 0.4f * pointMarkerScale;
                break;
            default:
                primitive = PrimitiveType.Cube;
                scale = Vector3.one * pointMarkerScale;
                yOffset = 0.5f * pointMarkerScale;
                break;
        }

        GameObject go = GameObject.CreatePrimitive(primitive);
        Undo.RegisterCreatedObjectUndo(go, "Generate OSM Marker");
        go.name = $"OSM_{item.Kind}_{item.SourceId}";
        go.transform.localScale = scale;
        go.transform.localPosition = new Vector3(item.Position.x, item.Position.y + yOffset, item.Position.z);

        Collider col = go.GetComponent<Collider>();
        if (col != null)
            DestroyImmediate(col);

        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr != null && material != null)
            mr.sharedMaterial = material;

        return go;
    }

    private static float ResolveRoadWidth(Dictionary<string, string> tags)
    {
        if (tags != null)
        {
            if (tags.TryGetValue("width", out string widthValue))
            {
                if (TryParseNumericMeters(widthValue, out float width) && width > 0f)
                    return Mathf.Max(0.5f, width);
            }

            if (tags.TryGetValue("highway", out string highwayValue) && highwayValue != null)
            {
                string v = highwayValue.Trim().ToLowerInvariant();
                switch (v)
                {
                    case "motorway": return 10f;
                    case "trunk": return 8f;
                    case "primary": return 7f;
                    case "secondary": return 6f;
                    case "tertiary": return 5f;
                    case "residential": return 4f;
                    case "unclassified": return 4f;
                    case "living_street": return 4f;
                    case "service": return 3f;
                    case "track": return 2.5f;
                    case "path": return 1.5f;
                    case "footway": return 1.5f;
                    case "cycleway": return 1.8f;
                }
            }
        }

        return 4f;
    }

    private static Mesh CombineMeshesPreserveSubmeshes(List<GeneratedMeshItem> items, string meshName)
    {
        if (items == null || items.Count == 0)
            return null;

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var wallTriangles = new List<int>();
        var roofTriangles = new List<int>();

        for (int i = 0; i < items.Count; i++)
        {
            Mesh mesh = items[i].Mesh;
            if (mesh == null)
                continue;

            Vector3[] srcVertices = mesh.vertices;
            Vector2[] srcUvs = mesh.uv;
            int vertexOffset = vertices.Count;

            vertices.AddRange(srcVertices);

            if (srcUvs != null && srcUvs.Length == srcVertices.Length)
                uvs.AddRange(srcUvs);
            else
            {
                for (int v = 0; v < srcVertices.Length; v++)
                    uvs.Add(Vector2.zero);
            }

            if (mesh.subMeshCount > 0)
            {
                int[] tris0 = mesh.GetTriangles(0);
                for (int t = 0; t < tris0.Length; t++)
                    wallTriangles.Add(tris0[t] + vertexOffset);
            }

            if (mesh.subMeshCount > 1)
            {
                int[] tris1 = mesh.GetTriangles(1);
                for (int t = 0; t < tris1.Length; t++)
                    roofTriangles.Add(tris1[t] + vertexOffset);
            }
        }

        if (vertices.Count == 0)
            return null;

        Mesh combined = new Mesh { name = meshName };
        if (vertices.Count > 65535)
            combined.indexFormat = IndexFormat.UInt32;

        combined.subMeshCount = 2;
        combined.SetVertices(vertices);
        combined.SetUVs(0, uvs);
        combined.SetTriangles(wallTriangles, 0, true);
        combined.SetTriangles(roofTriangles, 1, true);
        combined.RecalculateNormals();
        combined.RecalculateBounds();
        return combined;
    }

    private static Mesh CombineMeshesSingleSubmesh(List<GeneratedMeshItem> items, string meshName)
    {
        if (items == null || items.Count == 0)
            return null;

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        for (int i = 0; i < items.Count; i++)
        {
            Mesh mesh = items[i].Mesh;
            if (mesh == null)
                continue;

            Vector3[] srcVertices = mesh.vertices;
            Vector2[] srcUvs = mesh.uv;
            int vertexOffset = vertices.Count;

            vertices.AddRange(srcVertices);

            if (srcUvs != null && srcUvs.Length == srcVertices.Length)
                uvs.AddRange(srcUvs);
            else
            {
                for (int v = 0; v < srcVertices.Length; v++)
                    uvs.Add(Vector2.zero);
            }

            int[] tris = mesh.triangles;
            for (int t = 0; t < tris.Length; t++)
                triangles.Add(tris[t] + vertexOffset);
        }

        if (vertices.Count == 0)
            return null;

        Mesh combined = new Mesh { name = meshName };
        if (vertices.Count > 65535)
            combined.indexFormat = IndexFormat.UInt32;

        combined.subMeshCount = 1;
        combined.SetVertices(vertices);
        combined.SetUVs(0, uvs);
        combined.SetTriangles(triangles, 0, true);
        combined.RecalculateNormals();
        combined.RecalculateBounds();
        return combined;
    }

    private static Mesh BuildExtrudedBuildingMesh(List<Vector2> footprint, float height)
    {
        if (footprint == null || footprint.Count < 3)
            return null;

        List<Vector2> polygon = new List<Vector2>(footprint);
        float area = SignedArea(polygon);
        if (Mathf.Abs(area) < 0.001f)
            return null;

        if (area < 0f)
            polygon.Reverse();

        List<int> roofTriangles = TriangulatePolygon(polygon);
        if (roofTriangles == null || roofTriangles.Count < 3)
            return null;

        var vertices = new List<Vector3>(polygon.Count * 8);
        var uvs = new List<Vector2>(polygon.Count * 8);
        var wallTriangles = new List<int>(polygon.Count * 6);
        var roofTriangleIndices = new List<int>(roofTriangles.Count);

        for (int i = 0; i < polygon.Count; i++)
        {
            int next = (i + 1) % polygon.Count;
            Vector2 p0 = polygon[i];
            Vector2 p1 = polygon[next];

            Vector3 v0 = new Vector3(p0.x, 0f, p0.y);
            Vector3 v1 = new Vector3(p1.x, 0f, p1.y);
            Vector3 v2 = new Vector3(p1.x, height, p1.y);
            Vector3 v3 = new Vector3(p0.x, height, p0.y);

            int startIndex = vertices.Count;
            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);

            float edgeLength = Vector2.Distance(p0, p1);
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(edgeLength, 0f));
            uvs.Add(new Vector2(edgeLength, height));
            uvs.Add(new Vector2(0f, height));

            wallTriangles.Add(startIndex + 0);
            wallTriangles.Add(startIndex + 2);
            wallTriangles.Add(startIndex + 1);
            wallTriangles.Add(startIndex + 0);
            wallTriangles.Add(startIndex + 3);
            wallTriangles.Add(startIndex + 2);
        }

        float minX = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 p = polygon[i];
            if (p.x < minX) minX = p.x;
            if (p.y < minZ) minZ = p.y;
            if (p.x > maxX) maxX = p.x;
            if (p.y > maxZ) maxZ = p.y;
        }

        float sizeX = Mathf.Max(0.001f, maxX - minX);
        float sizeZ = Mathf.Max(0.001f, maxZ - minZ);

        int roofStart = vertices.Count;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 p = polygon[i];
            vertices.Add(new Vector3(p.x, height, p.y));
            uvs.Add(new Vector2((p.x - minX) / sizeX, (p.y - minZ) / sizeZ));
        }

        for (int i = 0; i < roofTriangles.Count; i += 3)
        {
            roofTriangleIndices.Add(roofStart + roofTriangles[i + 0]);
            roofTriangleIndices.Add(roofStart + roofTriangles[i + 1]);
            roofTriangleIndices.Add(roofStart + roofTriangles[i + 2]);
        }

        Mesh mesh = new Mesh { name = "OSM_Building_Mesh" };
        if (vertices.Count > 65535)
            mesh.indexFormat = IndexFormat.UInt32;

        mesh.subMeshCount = 2;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(wallTriangles, 0, true);
        mesh.SetTriangles(roofTriangleIndices, 1, true);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private Mesh BuildRoadMesh(List<Vector2> points, float width, float heightOffset)
    {
        if (points == null || points.Count < 2)
            return null;

        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        float halfWidth = Mathf.Max(0.05f, width * 0.5f);
        float cumulative = 0f;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[i + 1];
            Vector2 delta = b - a;
            float segLen = delta.magnitude;
            if (segLen < 0.001f)
                continue;

            Vector2 dir = delta / segLen;
            Vector2 perp = new Vector2(-dir.y, dir.x) * halfWidth;

            Vector3 v0 = new Vector3(a.x - perp.x, heightOffset, a.y - perp.y);
            Vector3 v1 = new Vector3(a.x + perp.x, heightOffset, a.y + perp.y);
            Vector3 v2 = new Vector3(b.x + perp.x, heightOffset, b.y + perp.y);
            Vector3 v3 = new Vector3(b.x - perp.x, heightOffset, b.y - perp.y);

            int start = vertices.Count;
            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);

            float u0 = cumulative;
            float u1 = cumulative + segLen;

            uvs.Add(new Vector2(u0, 0f));
            uvs.Add(new Vector2(u0, 1f));
            uvs.Add(new Vector2(u1, 1f));
            uvs.Add(new Vector2(u1, 0f));

            triangles.Add(start + 0);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start + 0);
            triangles.Add(start + 2);
            triangles.Add(start + 3);

            cumulative += segLen;
        }

        if (vertices.Count == 0)
            return null;

        Mesh mesh = new Mesh { name = "OSM_Road_Mesh" };
        if (vertices.Count > 65535)
            mesh.indexFormat = IndexFormat.UInt32;

        mesh.subMeshCount = 1;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0, true);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private Mesh BuildFlatPolygonMesh(List<Vector2> polygon, float y)
    {
        if (polygon == null || polygon.Count < 3)
            return null;

        List<Vector2> poly = new List<Vector2>(polygon);
        float area = SignedArea(poly);
        if (Mathf.Abs(area) < 0.001f)
            return null;

        if (area < 0f)
            poly.Reverse();

        List<int> tris = TriangulatePolygon(poly);
        if (tris == null || tris.Count < 3)
            return null;

        var vertices = new List<Vector3>(poly.Count);
        var uvs = new List<Vector2>(poly.Count);

        float minX = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i];
            if (p.x < minX) minX = p.x;
            if (p.y < minZ) minZ = p.y;
            if (p.x > maxX) maxX = p.x;
            if (p.y > maxZ) maxZ = p.y;
        }

        float sizeX = Mathf.Max(0.001f, maxX - minX);
        float sizeZ = Mathf.Max(0.001f, maxZ - minZ);

        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i];
            vertices.Add(new Vector3(p.x, y, p.y));
            uvs.Add(new Vector2((p.x - minX) / sizeX, (p.y - minZ) / sizeZ));
        }

        Mesh mesh = new Mesh { name = "OSM_Flat_Mesh" };
        if (vertices.Count > 65535)
            mesh.indexFormat = IndexFormat.UInt32;

        mesh.subMeshCount = 1;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0, true);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static float SignedArea(List<Vector2> polygon)
    {
        if (polygon == null || polygon.Count < 3)
            return 0f;

        double area = 0.0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector2 a = polygon[j];
            Vector2 b = polygon[i];
            area += (double)a.x * b.y - (double)b.x * a.y;
        }

        return (float)(area * 0.5);
    }

    private static List<int> TriangulatePolygon(List<Vector2> polygon)
    {
        if (polygon == null || polygon.Count < 3)
            return null;

        var triangles = new List<int>();
        int n = polygon.Count;
        int[] V = new int[n];

        if (SignedArea(polygon) > 0f)
        {
            for (int v = 0; v < n; v++)
                V[v] = v;
        }
        else
        {
            for (int v = 0; v < n; v++)
                V[v] = (n - 1) - v;
        }

        int nv = n;
        int count = 2 * nv;

        for (int m = 0, v = nv - 1; nv > 2;)
        {
            if ((count--) <= 0)
                return null;

            int u = v;
            if (nv <= u) u = 0;
            v = u + 1;
            if (nv <= v) v = 0;
            int w = v + 1;
            if (nv <= w) w = 0;

            if (Snip(polygon, u, v, w, nv, V))
            {
                int a = V[u];
                int b = V[v];
                int c = V[w];

                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);

                for (int s = v, t = v + 1; t < nv; s++, t++)
                    V[s] = V[t];

                nv--;
                count = 2 * nv;
            }
        }

        return triangles;
    }

    private static bool Snip(List<Vector2> contour, int u, int v, int w, int n, int[] V)
    {
        const float epsilon = 0.000001f;

        Vector2 A = contour[V[u]];
        Vector2 B = contour[V[v]];
        Vector2 C = contour[V[w]];

        float cross = Cross(A, B, C);
        if (cross <= epsilon)
            return false;

        for (int p = 0; p < n; p++)
        {
            if (p == u || p == v || p == w)
                continue;

            Vector2 P = contour[V[p]];
            if (PointInTriangle(P, A, B, C))
                return false;
        }

        return true;
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float s = Cross(p, a, b);
        float t = Cross(p, b, c);
        float u = Cross(p, c, a);

        bool hasNeg = (s < 0f) || (t < 0f) || (u < 0f);
        bool hasPos = (s > 0f) || (t > 0f) || (u > 0f);

        return !(hasNeg && hasPos);
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
    {
        return ((b.x - a.x) * (c.y - a.y)) - ((b.y - a.y) * (c.x - a.x));
    }

    private static Vector3 LatLonToLocalMercator(double lat, double lon, double centerLat, double centerLon)
    {
        const double R = 6378137.0;

        double latRad = DegreesToRadians(lat);
        double lonRad = DegreesToRadians(lon);
        double centerLatRad = DegreesToRadians(centerLat);
        double centerLonRad = DegreesToRadians(centerLon);

        double x = R * lonRad;
        double z = R * Math.Log(Math.Tan(Math.PI * 0.25 + latRad * 0.5));

        double cx = R * centerLonRad;
        double cz = R * Math.Log(Math.Tan(Math.PI * 0.25 + centerLatRad * 0.5));

        return new Vector3((float)(x - cx), 0f, (float)(z - cz));
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }

    private static float ResolveBuildingHeight(Dictionary<string, string> tags, float defaultFloorHeight)
    {
        const int fallbackFloors = 3;

        if (tags != null)
        {
            if (tags.TryGetValue("height", out string heightValue))
            {
                if (TryParseNumericMeters(heightValue, out float meters) && meters > 0f)
                    return meters;
            }

            if (tags.TryGetValue("building:levels", out string levelsValue))
            {
                if (TryParseNumericMeters(levelsValue, out float levels) && levels > 0f)
                    return Mathf.Max(0.1f, levels * Mathf.Max(0.1f, defaultFloorHeight));
            }
        }

        return Mathf.Max(0.1f, fallbackFloors * Mathf.Max(0.1f, defaultFloorHeight));
    }

    private static bool TryParseNumericMeters(string text, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        Match match = Regex.Match(text, @"-?\d+(?:[.,]\d+)?");
        if (!match.Success)
            return false;

        string number = match.Value.Replace(',', '.');
        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static Dictionary<Vector2Int, List<GeneratedMeshItem>> GroupIntoChunks(List<GeneratedMeshItem> items, float chunkSize)
    {
        var chunks = new Dictionary<Vector2Int, List<GeneratedMeshItem>>();
        if (items == null)
            return chunks;

        for (int i = 0; i < items.Count; i++)
        {
            GeneratedMeshItem item = items[i];
            Vector2Int key = new Vector2Int(
                Mathf.FloorToInt(item.CenterXZ.x / chunkSize),
                Mathf.FloorToInt(item.CenterXZ.y / chunkSize));

            if (!chunks.TryGetValue(key, out List<GeneratedMeshItem> list))
            {
                list = new List<GeneratedMeshItem>();
                chunks.Add(key, list);
            }

            list.Add(item);
        }

        return chunks;
    }

    private static void EnsureAssetFolders(string assetFolderPath)
    {
        string normalized = assetFolderPath.Replace('\\', '/').TrimEnd('/');

        if (!normalized.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Asset folder must be inside the Unity Assets folder.");

        EnsureFolderExists(normalized);
        EnsureFolderExists($"{normalized}/Meshes");
        EnsureFolderExists($"{normalized}/Materials");
    }

    private static void EnsureFolderExists(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath))
            return;

        string[] parts = folderPath.Split('/');
        if (parts.Length == 0 || !parts[0].Equals("Assets", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Invalid folder path: {folderPath}");

        string current = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = parts[i];
            string candidate = $"{current}/{next}";

            if (!AssetDatabase.IsValidFolder(candidate))
                AssetDatabase.CreateFolder(current, next);

            current = candidate;
        }
    }

    private Mesh SaveMeshAsset(Mesh mesh, string folderPath, string baseName)
    {
        if (mesh == null)
            return null;

        string safeName = SanitizeFileName(baseName);
        string assetPath = $"{folderPath.TrimEnd('/')}/{safeName}.asset";
        assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

        AssetDatabase.CreateAsset(mesh, assetPath);
        return mesh;
    }

    private Material SaveMaterialAsset(Material material, string folderPath, string baseName)
    {
        if (material == null)
            return null;

        string safeName = SanitizeFileName(baseName);
        string assetPath = $"{folderPath.TrimEnd('/')}/{safeName}.mat";
        assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

        AssetDatabase.CreateAsset(material, assetPath);
        return material;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Asset";

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), "_");

        return name;
    }

    private static TextureOverrideEntry GetTextureOverride(long osmId, List<TextureOverrideEntry> overrides)
    {
        if (overrides == null)
            return null;

        for (int i = 0; i < overrides.Count; i++)
        {
            TextureOverrideEntry entry = overrides[i];
            if (entry != null && entry.OsmId == osmId)
                return entry;
        }

        return null;
    }

    private TextureOverrideEntry GetTextureOverride(long osmId)
    {
        return GetTextureOverride(osmId, textureOverrides);
    }

    private static string FormatPointKind(PointKind kind)
    {
        switch (kind)
        {
            case PointKind.Tree: return "Tree";
            case PointKind.Bench: return "Bench";
            case PointKind.Fountain: return "Fountain";
            case PointKind.Well: return "Well";
            case PointKind.Lamp: return "Lamp";
            case PointKind.Monument: return "Monument";
            case PointKind.Info: return "Info";
            default: return "Generic";
        }
    }
}

internal sealed class OSMNode
{
    public long Id;
    public double Lat;
    public double Lon;
}

internal sealed class OSMWay
{
    public long Id;
    public List<long> NodeIds = new List<long>();
    public Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class OSMRelationMember
{
    public string Type;
    public long RefId;
    public string Role;
}

internal sealed class OSMRelation
{
    public long Id;
    public List<OSMRelationMember> Members = new List<OSMRelationMember>();
    public Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class OverpassData
{
    public Dictionary<long, OSMNode> Nodes = new Dictionary<long, OSMNode>();
    public Dictionary<long, OSMWay> Ways = new Dictionary<long, OSMWay>();
    public Dictionary<long, OSMRelation> Relations = new Dictionary<long, OSMRelation>();

    public Dictionary<long, OSMNode> BuildNodeLookup()
    {
        return Nodes;
    }
}

internal static class OverpassJsonParser
{
    public static OverpassData Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new Exception("Empty JSON response.");

        object rootObj = MiniJson.Deserialize(json);
        if (!(rootObj is Dictionary<string, object> root))
            throw new Exception("Invalid Overpass JSON root object.");

        if (!root.TryGetValue("elements", out object elementsObj) || !(elementsObj is List<object> elements))
            throw new Exception("Overpass JSON does not contain 'elements' array.");

        var data = new OverpassData();

        for (int i = 0; i < elements.Count; i++)
        {
            if (!(elements[i] is Dictionary<string, object> element))
                continue;

            string type = GetString(element, "type");
            long id = GetLong(element, "id");

            switch (type)
            {
                case "node":
                {
                    if (!element.ContainsKey("lat") || !element.ContainsKey("lon"))
                        continue;

                    data.Nodes[id] = new OSMNode
                    {
                        Id = id,
                        Lat = GetDouble(element, "lat"),
                        Lon = GetDouble(element, "lon")
                    };
                    break;
                }
                case "way":
                {
                    var way = new OSMWay { Id = id };

                    if (element.TryGetValue("nodes", out object nodesObj) && nodesObj is List<object> nodeList)
                    {
                        for (int n = 0; n < nodeList.Count; n++)
                            way.NodeIds.Add(GetLong(nodeList[n]));
                    }

                    if (element.TryGetValue("tags", out object tagsObj) && tagsObj is Dictionary<string, object> tagsDict)
                        way.Tags = ToStringDictionary(tagsDict);

                    data.Ways[id] = way;
                    break;
                }
                case "relation":
                {
                    var relation = new OSMRelation { Id = id };

                    if (element.TryGetValue("members", out object membersObj) && membersObj is List<object> membersList)
                    {
                        for (int m = 0; m < membersList.Count; m++)
                        {
                            if (!(membersList[m] is Dictionary<string, object> memberDict))
                                continue;

                            relation.Members.Add(new OSMRelationMember
                            {
                                Type = GetString(memberDict, "type"),
                                RefId = GetLong(memberDict, "ref"),
                                Role = GetString(memberDict, "role")
                            });
                        }
                    }

                    if (element.TryGetValue("tags", out object relTagsObj) && relTagsObj is Dictionary<string, object> relTagsDict)
                        relation.Tags = ToStringDictionary(relTagsDict);

                    data.Relations[id] = relation;
                    break;
                }
            }
        }

        return data;
    }

    private static Dictionary<string, string> ToStringDictionary(Dictionary<string, object> source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, object> kv in source)
            result[kv.Key] = kv.Value != null ? kv.Value.ToString() : string.Empty;
        return result;
    }

    private static string GetString(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out object obj) || obj == null)
            return string.Empty;
        return obj.ToString();
    }

    private static long GetLong(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out object obj))
            return 0L;
        return GetLong(obj);
    }

    private static long GetLong(object obj)
    {
        if (obj == null)
            return 0L;

        if (obj is long l) return l;
        if (obj is int i) return i;
        if (obj is double d) return (long)d;
        if (obj is float f) return (long)f;

        string s = obj.ToString();
        if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out long parsedLong))
            return parsedLong;

        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedDouble))
            return (long)parsedDouble;

        return 0L;
    }

    private static double GetDouble(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out object obj))
            return 0.0;

        if (obj is double d) return d;
        if (obj is float f) return f;
        if (obj is int i) return i;
        if (obj is long l) return l;

        string s = obj.ToString().Replace(',', '.');
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
            return parsed;

        return 0.0;
    }
}

internal static class MiniJson
{
    public static object Deserialize(string json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        using (var parser = new Parser(json))
        {
            return parser.ParseValue();
        }
    }

    private sealed class Parser : IDisposable
    {
        private readonly StringReader json;

        public Parser(string jsonString)
        {
            json = new StringReader(jsonString);
        }

        public void Dispose()
        {
            json.Dispose();
        }

        public object ParseValue()
        {
            EatWhitespace();

            int c = json.Peek();
            if (c == -1)
                return null;

            switch ((char)c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return ParseString();
                case 't':
                case 'f':
                case 'n':
                    return ParseWord();
                default:
                    return ParseNumber();
            }
        }

        private Dictionary<string, object> ParseObject()
        {
            var table = new Dictionary<string, object>(StringComparer.Ordinal);
            json.Read(); // {

            while (true)
            {
                EatWhitespace();

                int c = json.Peek();
                if (c == -1)
                    return table;

                if ((char)c == '}')
                {
                    json.Read();
                    return table;
                }

                string key = ParseString();
                EatWhitespace();

                if (json.Read() != ':')
                    throw new Exception("Invalid JSON object: expected ':'.");

                object value = ParseValue();
                table[key] = value;

                EatWhitespace();
                c = json.Peek();
                if (c == ',')
                {
                    json.Read();
                    continue;
                }

                if (c == '}')
                {
                    json.Read();
                    return table;
                }
            }
        }

        private List<object> ParseArray()
        {
            var array = new List<object>();
            json.Read(); // [

            while (true)
            {
                EatWhitespace();

                int c = json.Peek();
                if (c == -1)
                    return array;

                if ((char)c == ']')
                {
                    json.Read();
                    return array;
                }

                object value = ParseValue();
                array.Add(value);

                EatWhitespace();
                c = json.Peek();
                if (c == ',')
                {
                    json.Read();
                    continue;
                }

                if (c == ']')
                {
                    json.Read();
                    return array;
                }
            }
        }

        private object ParseWord()
        {
            string word = NextWord();
            switch (word)
            {
                case "true": return true;
                case "false": return false;
                case "null": return null;
                default: throw new Exception($"Invalid JSON word: {word}");
            }
        }

        private string ParseString()
        {
            var sb = new StringBuilder();
            if (json.Read() != '"')
                throw new Exception("Invalid JSON string.");

            bool escape = false;
            while (true)
            {
                int c = json.Read();
                if (c == -1)
                    throw new Exception("Unterminated JSON string.");

                char ch = (char)c;

                if (escape)
                {
                    switch (ch)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                        {
                            char[] hex = new char[4];
                            for (int i = 0; i < 4; i++)
                            {
                                int h = json.Read();
                                if (h == -1)
                                    throw new Exception("Invalid unicode escape.");
                                hex[i] = (char)h;
                            }

                            if (!ushort.TryParse(new string(hex), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                                throw new Exception("Invalid unicode escape.");

                            sb.Append((char)code);
                            break;
                        }
                        default:
                            sb.Append(ch);
                            break;
                    }

                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                    continue;
                }

                if (ch == '"')
                    return sb.ToString();

                sb.Append(ch);
            }
        }

        private object ParseNumber()
        {
            string number = NextNumberToken();

            if (number.IndexOf('.') >= 0 || number.IndexOf('e') >= 0 || number.IndexOf('E') >= 0)
            {
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return d;
            }
            else
            {
                if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return l;

                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double d2))
                    return d2;
            }

            throw new Exception($"Invalid JSON number: {number}");
        }

        private void EatWhitespace()
        {
            while (true)
            {
                int c = json.Peek();
                if (c == -1)
                    return;

                if (!char.IsWhiteSpace((char)c))
                    return;

                json.Read();
            }
        }

        private string NextWord()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int c = json.Peek();
                if (c == -1)
                    break;

                char ch = (char)c;
                if (IsWordBreak(ch))
                    break;

                sb.Append(ch);
                json.Read();
            }
            return sb.ToString();
        }

        private string NextNumberToken()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int c = json.Peek();
                if (c == -1)
                    break;

                char ch = (char)c;
                if (IsWordBreak(ch))
                    break;

                sb.Append(ch);
                json.Read();
            }
            return sb.ToString();
        }

        private static bool IsWordBreak(char c)
        {
            return char.IsWhiteSpace(c) || c == ',' || c == ':' || c == ']' || c == '}' || c == '[' || c == '{' || c == '"';
        }
    }
}

internal static class SimpleEditorCoroutineRunner
{
    private sealed class RoutineState
    {
        public IEnumerator Enumerator;
        public object Current;
    }

    private static readonly List<RoutineState> Routines = new List<RoutineState>();

    static SimpleEditorCoroutineRunner()
    {
        EditorApplication.update += Update;
    }

    public static void Start(IEnumerator routine)
    {
        if (routine == null)
            return;

        Routines.Add(new RoutineState { Enumerator = routine });
    }

    private static void Update()
    {
        for (int i = Routines.Count - 1; i >= 0; i--)
        {
            RoutineState state = Routines[i];
            try
            {
                if (state.Current is AsyncOperation asyncOperation)
                {
                    if (!asyncOperation.isDone)
                        continue;

                    state.Current = null;
                }

                bool moved = state.Enumerator.MoveNext();
                if (!moved)
                {
                    DisposeAt(i);
                    continue;
                }

                state.Current = state.Enumerator.Current;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                DisposeAt(i);
            }
        }
    }

    private static void DisposeAt(int index)
    {
        try
        {
            Routines[index].Enumerator.Dispose();
        }
        catch
        {
        }

        Routines.RemoveAt(index);
    }
}
#endif
