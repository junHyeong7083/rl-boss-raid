using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;      // SceneManager.GetActiveScene
using UnityEngine.Rendering;            // AmbientMode 등
using Object = UnityEngine.Object;      // System.Object 와의 모호성 방지

namespace BossRaid.EditorTools
{
    /// <summary>
    /// 보스 아레나 자동 구성 에디터 툴.
    /// - 바닥(원형 메시) / 외곽 링(기둥·소품) / 조명(어두운 레이드 홀) / 씬 컴포넌트 세팅을
    ///   "Arena_Generated" 루트 아래에 멱등하게 생성한다(재실행 시 기존 루트 삭제 후 재생성).
    /// - CC0 에셋(Assets/08_Env/)은 폴더 스캔으로 찾고, 없으면 프리미티브/기본값 fallback.
    /// </summary>
    public static class BossArenaBuilder
    {
        // ─────────────── 상수 ───────────────
        private const string ScenePath  = "Assets/Scenes/Boss/BossStage.unity";
        private const string RootName   = "Arena_Generated";
        private const string MatFolder  = "Assets/04_Material/Arena";

        // 아레나 중심 = ContinuousToWorld(10,10) = (10,0,10) (기본 cellSize=1, origin=0)
        private static readonly Vector3 Center = new Vector3(10f, 0f, 10f);
        private const float FloorRadius = 16f;

        // 08_Env 에셋 폴더 (절대 경로 계산은 Application.dataPath 기준)
        private static string EnvFloorDir  => Path.Combine(Application.dataPath, "08_Env/Textures/Floor");
        private static string EnvModelsDir => Path.Combine(Application.dataPath, "08_Env/Models");
        private static string EnvSkyDir    => Path.Combine(Application.dataPath, "08_Env/Sky");

        // ─────────────── 진입점 ───────────────

        [MenuItem("Tools/Boss Arena/Build Arena")]
        private static void BuildArenaMenu()
        {
            BuildArena();
            Debug.Log("[BossArenaBuilder] Build Arena 완료 (씬 저장은 수동으로).");
        }

        /// <summary>배치 모드용: 씬 열기 → 빌드 → 씬 저장. 실패 시 Exit(1).</summary>
        public static void BuildArenaBatch()
        {
            try
            {
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                BuildArena();
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[BossArenaBuilder] BuildArenaBatch 완료 및 씬 저장.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BossArenaBuilder] BuildArenaBatch 실패: {e}");
                EditorApplication.Exit(1);
            }
        }

        // ─────────────── 메인 빌드 ───────────────

        /// <summary>현재 활성 씬에 아레나를 (재)구성. 씬을 dirty 처리하되 저장은 하지 않음.</summary>
        public static void BuildArena()
        {
            var scene = SceneManager.GetActiveScene();

            // 1) 멱등: 기존 루트 제거 후 재생성
            var old = FindInScene(RootName);
            if (old != null) Object.DestroyImmediate(old);
            var root = new GameObject(RootName);

            var viewer = Object.FindFirstObjectByType<BossGameViewer>();

            BuildFloor(root.transform);
            BuildOuterRing(root.transform);
            BuildGameplayPillars(root.transform);
            BuildLighting(root.transform);
            BuildBloodMoon(root.transform);
            SetupSceneComponents(viewer);

            EditorSceneManager.MarkSceneDirty(scene);
        }

        // ─────────────── 1. 바닥 ───────────────

        private static void BuildFloor(Transform parent)
        {
            // 기존 "Plane"은 삭제하지 않고 비활성화만
            var plane = FindInScene("Plane");
            if (plane != null) plane.SetActive(false);

            // 원형 바닥 메시(fan triangulation, 월드 XZ 기반 UV 타일링 ~5회)
            float uvScale = 5f / (FloorRadius * 2f);
            var mesh = BuildCircleMesh(FloorRadius, 96, uvScale);

            var floor = new GameObject("Arena_Floor");
            floor.transform.SetParent(parent, false);
            floor.transform.position = Center;
            var mf = floor.AddComponent<MeshFilter>();
            var mr = floor.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;
            mr.sharedMaterial = BuildFloorMaterial();
        }

        /// <summary>중심 정점 + 링 정점의 fan 삼각화(양면). UV는 로컬 XZ × uvScale.</summary>
        private static Mesh BuildCircleMesh(float radius, int seg, float uvScale)
        {
            var verts = new Vector3[seg + 1];
            var uvs = new Vector2[seg + 1];
            var normals = new Vector3[seg + 1];
            verts[0] = Vector3.zero;
            uvs[0] = Vector2.zero;
            normals[0] = Vector3.up;

            for (int i = 0; i < seg; i++)
            {
                float a = (float)i / seg * Mathf.PI * 2f;
                var p = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                verts[i + 1] = p;
                normals[i + 1] = Vector3.up;
                uvs[i + 1] = new Vector2(p.x * uvScale, p.z * uvScale);
            }

            var tris = new List<int>(seg * 6);
            for (int i = 0; i < seg; i++)
            {
                int a = i + 1;
                int b = (i + 1) % seg + 1;
                tris.Add(0); tris.Add(a); tris.Add(b);   // 윗면
                tris.Add(0); tris.Add(b); tris.Add(a);   // 아랫면(양면 처리, 컬링 무관)
            }

            var mesh = new Mesh { name = "Arena_FloorMesh" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>URP Lit 머티리얼 생성 + Floor 텍스처 폴더 스캔 적용. 04_Material/Arena/에 저장.</summary>
        private static Material BuildFloorMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader) { name = "Mat_ArenaFloor" };

            // baseMap / normalMap(_BumpMap) / occlusionMap(AO) 만 적용 (roughness/metallic 미사용)
            string baseRel   = ScanFirst(EnvFloorDir, TextureExts, "Color", "BaseColor", "Albedo", "Diffuse");
            string normalRel = ScanFirst(EnvFloorDir, TextureExts, "Normal", "_N");
            string aoRel     = ScanFirst(EnvFloorDir, TextureExts, "AO", "Occlusion", "Ambient");

            if (baseRel != null)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(baseRel);
                if (tex != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            }
            if (normalRel != null)
            {
                EnsureNormalMapImport(normalRel);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(normalRel);
                if (tex != null && mat.HasProperty("_BumpMap"))
                {
                    mat.SetTexture("_BumpMap", tex);
                    mat.EnableKeyword("_NORMALMAP");
                }
            }
            if (aoRel != null)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(aoRel);
                if (tex != null && mat.HasProperty("_OcclusionMap"))
                {
                    mat.SetTexture("_OcclusionMap", tex);
                    mat.EnableKeyword("_OCCLUSIONMAP");
                    if (mat.HasProperty("_OcclusionStrength")) mat.SetFloat("_OcclusionStrength", 1f);
                }
            }

            // 텍스처 타일링(월드 XZ UV는 이미 반복되므로 tiling 1)
            // 혈월 테마: 바닥에 살짝 핏기(붉은 틴트)
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(1.0f, 0.82f, 0.80f));
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", new Color(1.0f, 0.82f, 0.80f));
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.25f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            SaveMaterialAsset(mat, "Mat_ArenaFloor.mat");
            return mat;
        }

        // ─────────────── 2. 외곽 링 ───────────────

        private static void BuildOuterRing(Transform parent)
        {
            var models = ScanModels();
            var pillarModels = models.FindAll(m =>
                ContainsAny(Path.GetFileNameWithoutExtension(m), "pillar", "column"));
            var propModels = new List<string>(models);
            // 기둥 후보를 소품 풀에서 우선 제거(가능하면 소품은 그 외 모델 사용)
            foreach (var p in pillarModels) propModels.Remove(p);

            var pillarRing = new GameObject("Pillars").transform;
            pillarRing.SetParent(parent, false);

            // 기둥 12개, 반지름 15, 중심을 바라보게
            const int pillarCount = 12;
            const float pillarR = 15f;
            for (int i = 0; i < pillarCount; i++)
            {
                float ang = (float)i / pillarCount * Mathf.PI * 2f;
                var pos = Center + new Vector3(Mathf.Cos(ang) * pillarR, 0f, Mathf.Sin(ang) * pillarR);
                GameObject prefab = null;
                if (pillarModels.Count > 0) prefab = LoadModel(pillarModels[i % pillarModels.Count]);
                else if (models.Count > 0)   prefab = LoadModel(models[i % models.Count]);
                var go = PlaceModel(prefab, pos, pillarRing, UnityEngine.Random.Range(3f, 5f),
                                    PrimitiveType.Cylinder, faceCenter: true, randomYaw: false);
                go.name = $"Pillar_{i:00}";
            }

            // 소품(바위/벽) 8~12개, 반지름 15.5~17, 고정 시드 42
            var propsRoot = new GameObject("Props").transform;
            propsRoot.SetParent(parent, false);
            var rng = new System.Random(42);
            int propCount = 10;
            for (int i = 0; i < propCount; i++)
            {
                float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                float r = 15.5f + (float)rng.NextDouble() * 1.5f;   // 15.5~17
                var pos = Center + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                GameObject prefab = null;
                var pool = propModels.Count > 0 ? propModels : models;
                if (pool.Count > 0) prefab = LoadModel(pool[rng.Next(pool.Count)]);
                float h = 1f + (float)rng.NextDouble() * 1.5f;      // 1~2.5
                var go = PlaceModel(prefab, pos, propsRoot, h, PrimitiveType.Cube,
                                    faceCenter: false, randomYaw: true, rng: rng);
                go.name = $"Prop_{i:00}";
            }
        }

        /// <summary>모델(또는 프리미티브)을 배치하고 바운즈 기준으로 높이를 정규화.</summary>
        private static GameObject PlaceModel(GameObject prefab, Vector3 pos, Transform parent,
            float targetHeight, PrimitiveType fallback, bool faceCenter, bool randomYaw,
            System.Random rng = null)
        {
            GameObject go;
            if (prefab != null)
            {
                go = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (go == null) go = Object.Instantiate(prefab);
            }
            else
            {
                go = GameObject.CreatePrimitive(fallback);
            }

            go.transform.SetParent(parent, true);
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;

            // 바운즈 기준 높이 정규화 (원점/identity 상태에서 측정)
            var b = ComputeBounds(go);
            float sizeY = Mathf.Max(0.01f, b.size.y);
            float scale = targetHeight / sizeY;
            go.transform.localScale *= scale;

            // 바닥에 앉히기: 스케일 적용 후 바운즈 최저점을 y=0으로
            float bottom = b.min.y * scale;
            go.transform.position = new Vector3(pos.x, pos.y - bottom, pos.z);

            // 회전
            if (faceCenter)
            {
                var dir = Center - pos; dir.y = 0f;
                if (dir.sqrMagnitude > 1e-4f)
                    go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }
            else if (randomYaw)
            {
                float y = rng != null ? (float)rng.NextDouble() * 360f : 0f;
                go.transform.rotation = Quaternion.Euler(0f, y, 0f);
            }
            return go;
        }

        // ─────────────── 2.5 게임플레이 기둥 (런타임 연동) ───────────────

        /// <summary>
        /// 시뮬 좌표 (5,5)/(15,5)/(5,15)/(15,15) = 월드 (5,0,5)/(15,0,5)/(5,0,15)/(15,0,15) 에
        /// 파괴 가능 게임플레이 기둥 4개 생성. Python env의 돌진 유도/전멸기 은신 기둥 좌표와 동일.
        /// 이름은 GameplayPillar_0~3 (런타임 파괴 상태 연동 예정), "GameplayPillars" 그룹 하위.
        /// </summary>
        private static void BuildGameplayPillars(Transform parent)
        {
            var models = ScanModels();
            // rocks-tall 우선 → pillar/column 계열 → fallback Cylinder
            string chosen = models.Find(m =>
                ContainsAny(Path.GetFileNameWithoutExtension(m), "rocks-tall"));
            if (chosen == null)
                chosen = models.Find(m =>
                    ContainsAny(Path.GetFileNameWithoutExtension(m), "pillar", "column"));

            var group = new GameObject("GameplayPillars").transform;
            group.SetParent(parent, false);

            var positions = new[]
            {
                new Vector3( 5f, 0f,  5f),
                new Vector3(15f, 0f,  5f),
                new Vector3( 5f, 0f, 15f),
                new Vector3(15f, 0f, 15f),
            };
            for (int i = 0; i < positions.Length; i++)
            {
                var prefab = LoadModel(chosen);
                var go = PlaceModel(prefab, positions[i], group, 4.5f, PrimitiveType.Cylinder,
                                    faceCenter: false, randomYaw: false);
                NormalizeRadius(go, positions[i], 1.2f);   // 시각 반경 ~1.2
                go.name = $"GameplayPillar_{i}";
            }
        }

        /// <summary>XZ 스케일을 조정해 시각 반경을 targetRadius로 맞추고 바닥(min.y=groundPos.y)에 재정렬.</summary>
        private static void NormalizeRadius(GameObject go, Vector3 groundPos, float targetRadius)
        {
            var b = ComputeBounds(go);
            float curR = Mathf.Max(0.01f, Mathf.Max(b.size.x, b.size.z) * 0.5f);
            float f = targetRadius / curR;
            var s = go.transform.localScale;
            go.transform.localScale = new Vector3(s.x * f, s.y, s.z * f);

            var b2 = ComputeBounds(go);
            float delta = groundPos.y - b2.min.y;
            go.transform.position += new Vector3(0f, delta, 0f);
        }

        // ─────────────── 3. 조명 ───────────────

        private static void BuildLighting(Transform parent)
        {
            // 스카이박스 (Sky/*.hdr → 큐브맵 재임포트 → Skybox/Cubemap 머티리얼)
            var skybox = BuildSkyboxMaterial();
            if (skybox != null)
            {
                // 스카이박스는 배경 비주얼용으로만. 달 없는 밤 HDRI는 거의 검정이라
                // Skybox 앰비언트 모드를 쓰면 씬 전체가 실루엣이 됨 → 앰비언트는 Trilight로 직접 제어.
                RenderSettings.skybox = skybox;
            }
            // 앰비언트: 혈월 Trilight (붉은 하늘 / 어두운 핏빛 지면)
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor     = new Color(0.38f, 0.20f, 0.22f);
            RenderSettings.ambientEquatorColor = new Color(0.22f, 0.13f, 0.15f);
            RenderSettings.ambientGroundColor  = new Color(0.10f, 0.06f, 0.07f);

            // 기존 Directional Light 혈월화 (값만 수정 — 오브젝트 삭제/생성 금지).
            // 붉은 달 방향(+X/+Z 원거리)에서 내리쬐는 핏빛 역광 느낌.
            var sun = FindDirectionalLight();
            if (sun != null)
            {
                sun.color = new Color(0.95f, 0.55f, 0.5f);
                sun.intensity = 0.55f;
                sun.transform.rotation = Quaternion.Euler(55f, 210f, 0f);
            }

            // 외곽 횃불 포인트 라이트 4개: 사분면 기둥 근처, 깊은 혈색
            var lightsRoot = new GameObject("PointLights").transform;
            lightsRoot.SetParent(parent, false);
            var quads = new[]
            {
                new Vector3( 10f, 6f,  10f),
                new Vector3( 10f, 6f, -10f),
                new Vector3(-10f, 6f,  10f),
                new Vector3(-10f, 6f, -10f),
            };
            for (int i = 0; i < quads.Length; i++)
            {
                var lgo = new GameObject($"PointLight_{i}");
                lgo.transform.SetParent(lightsRoot, false);
                lgo.transform.position = Center + quads[i];
                var l = lgo.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1.0f, 0.42f, 0.22f);
                l.intensity = 45f;   // ACES 톤매핑 아래서 존재감 있게
                l.range = 20f;
            }

            // 센터 라이트: 전투 중심부(보스/유닛 가독성) 확보용 살짝 보라 기 상단 광
            var cgo = new GameObject("CenterLight");
            cgo.transform.SetParent(lightsRoot, false);
            cgo.transform.position = Center + new Vector3(0f, 8f, 0f);
            var cl = cgo.AddComponent<Light>();
            cl.type = LightType.Point;
            cl.color = new Color(0.9f, 0.85f, 0.95f);
            cl.intensity = 30f;
            cl.range = 18f;

            // 카메라 방향 필 라이트: 수직면(캐릭터 정면) 가독성 확보 — 실루엣화 방지, 그림자 없음
            var fgo = new GameObject("CameraFillLight");
            fgo.transform.SetParent(lightsRoot, false);
            fgo.transform.rotation = Quaternion.Euler(35f, 0f, 0f);   // 카메라(남→북 부감)와 유사 방향
            var fl = fgo.AddComponent<Light>();
            fl.type = LightType.Directional;
            fl.color = new Color(0.75f, 0.78f, 0.92f);
            fl.intensity = 0.9f;   // 0.4는 캐릭터(어두운 팔레트 텍스처)에 부족 (캡처 검수)
            fl.shadows = LightShadows.None;

            // 포그: 어두운 핏빛 지수 감쇠
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.014f;
            RenderSettings.fogColor = new Color(0.10f, 0.045f, 0.05f);
        }

        /// <summary>씬의 Directional Light(태양광) 검색. 이름 우선, 없으면 타입으로 스캔.</summary>
        private static Light FindDirectionalLight()
        {
            var byName = FindInScene("Directional Light");
            if (byName != null)
            {
                var l = byName.GetComponent<Light>();
                if (l != null && l.type == LightType.Directional) return l;
            }
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) return l;
            return null;
        }

        // ─────────────── 3.5 붉은 달 (혈월) ───────────────

        /// <summary>
        /// 광각/시네마틱 샷에서 하늘에 보이는 붉은 달. 아레나 중심 기준 (+35,22,+55) 위치에
        /// 지름 ~12 Sphere + 은은한 역광용 붉은 포인트 라이트. Unlit HDR 머티리얼로 블룸에 탐.
        /// </summary>
        private static void BuildBloodMoon(Transform parent)
        {
            var moonRoot = new GameObject("BloodMoon").transform;
            moonRoot.SetParent(parent, false);
            var moonPos = Center + new Vector3(35f, 22f, 55f);

            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "BloodMoonSphere";
            sphere.transform.SetParent(moonRoot, false);
            sphere.transform.position = moonPos;
            sphere.transform.localScale = Vector3.one * 12f;   // 지름 ~12
            var col = sphere.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
            sphere.GetComponent<MeshRenderer>().sharedMaterial = BuildBloodMoonMaterial();

            // 달 주변 붉은 글로우용 포인트 라이트 (은은한 역광)
            var lgo = new GameObject("BloodMoonGlow");
            lgo.transform.SetParent(moonRoot, false);
            lgo.transform.position = moonPos;
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1.0f, 0.3f, 0.25f);
            l.intensity = 5f;
            l.range = 30f;
        }

        /// <summary>URP Unlit + [HDR] 붉은 발광색. 블룸에 은은히 타도록 04_Material/Arena/에 저장.</summary>
        private static Material BuildBloodMoonMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            var mat = new Material(shader) { name = "Mat_BloodMoon" };
            var hdr = new Color(2.2f, 0.5f, 0.4f);   // HDR — 블룸 유발
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", hdr);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", hdr);
            SaveMaterialAsset(mat, "Mat_BloodMoon.mat");
            return mat;
        }

        /// <summary>Sky 폴더의 첫 .hdr 를 큐브맵으로 재임포트해 Skybox/Cubemap 머티리얼 생성. 없으면 null.</summary>
        private static Material BuildSkyboxMaterial()
        {
            string hdrRel = ScanFirst(EnvSkyDir, new[] { ".hdr", ".exr" });
            if (hdrRel == null) return null;

            // 큐브맵 셰이프로 재임포트
            var imp = AssetImporter.GetAtPath(hdrRel) as TextureImporter;
            if (imp != null && imp.textureShape != TextureImporterShape.TextureCube)
            {
                imp.textureShape = TextureImporterShape.TextureCube;
                imp.SaveAndReimport();
            }

            var cube = AssetDatabase.LoadAssetAtPath<Texture>(hdrRel);
            if (cube == null) return null;

            var sky = Shader.Find("Skybox/Cubemap");
            if (sky == null) return null;
            var mat = new Material(sky) { name = "Mat_ArenaSky" };
            if (mat.HasProperty("_Tex")) mat.SetTexture("_Tex", cube);
            if (mat.HasProperty("_Exposure")) mat.SetFloat("_Exposure", 0.6f);

            SaveMaterialAsset(mat, "Mat_ArenaSky.mat");
            return mat;
        }

        /// <summary>고정 경로에 머티리얼 에셋 저장. 재실행 시 기존 에셋 삭제 후 재생성(누적 방지).</summary>
        private static void SaveMaterialAsset(Material mat, string fileName)
        {
            EnsureFolder(MatFolder);
            string matPath = MatFolder + "/" + fileName;
            if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null)
                AssetDatabase.DeleteAsset(matPath);
            AssetDatabase.CreateAsset(mat, matPath);
        }

        // ─────────────── 4. 씬 컴포넌트 세팅 ───────────────

        private static void SetupSceneComponents(BossGameViewer viewer)
        {
            // Main Camera: LostArkCamera 부착 + viewer 참조 연결(SerializedObject)
            var camGo = FindInScene("Main Camera");
            if (camGo == null && Camera.main != null) camGo = Camera.main.gameObject;
            if (camGo != null)
            {
                var lac = camGo.GetComponent<LostArkCamera>();
                if (lac == null) lac = camGo.AddComponent<LostArkCamera>();
                if (viewer != null)
                {
                    var so = new SerializedObject(lac);
                    var prop = so.FindProperty("viewer");
                    if (prop != null)
                    {
                        prop.objectReferenceValue = viewer;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }

            // BossPostFX GameObject 없으면 생성 + 컴포넌트 부착
            var fxGo = FindInScene("BossPostFX");
            if (fxGo == null)
            {
                fxGo = new GameObject("BossPostFX");
            }
            if (fxGo.GetComponent<BossPostFX>() == null) fxGo.AddComponent<BossPostFX>();
        }

        // ─────────────── 유틸: 에셋 스캔 ───────────────

        private static readonly string[] TextureExts =
            { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".psd", ".exr", ".hdr" };
        private static readonly string[] ModelExts = { ".fbx", ".obj" };

        /// <summary>폴더에서 확장자 집합 + 이름 패턴(우선순위 순)에 맞는 첫 파일의 프로젝트 상대경로 반환.</summary>
        private static string ScanFirst(string absFolder, string[] exts, params string[] namePatterns)
        {
            if (!Directory.Exists(absFolder)) return null;
            var files = new List<string>();
            foreach (var f in Directory.GetFiles(absFolder, "*.*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(exts, ext) < 0) continue;
                files.Add(f);
            }
            if (files.Count == 0) return null;

            // 이름 패턴이 없으면 첫 파일
            if (namePatterns == null || namePatterns.Length == 0)
                return ToProjectRel(files[0]);

            foreach (var pat in namePatterns)
                foreach (var f in files)
                    if (Path.GetFileNameWithoutExtension(f).IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                        return ToProjectRel(f);
            return null;
        }

        /// <summary>Models 폴더의 모든 FBX/OBJ 프로젝트 상대경로 목록.</summary>
        private static List<string> ScanModels()
        {
            var result = new List<string>();
            if (!Directory.Exists(EnvModelsDir)) return result;
            foreach (var f in Directory.GetFiles(EnvModelsDir, "*.*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(ModelExts, ext) < 0) continue;
                result.Add(ToProjectRel(f));
            }
            return result;
        }

        private static GameObject LoadModel(string rel)
            => rel == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(rel);

        private static void EnsureNormalMapImport(string rel)
        {
            var imp = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (imp != null && imp.textureType != TextureImporterType.NormalMap)
            {
                imp.textureType = TextureImporterType.NormalMap;
                imp.SaveAndReimport();
            }
        }

        // ─────────────── 유틸: 공통 ───────────────

        private static string ToProjectRel(string absPath)
        {
            absPath = absPath.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (absPath.StartsWith(data, StringComparison.OrdinalIgnoreCase))
                return "Assets" + absPath.Substring(data.Length);
            return absPath;
        }

        private static void EnsureFolder(string projectPath)
        {
            if (AssetDatabase.IsValidFolder(projectPath)) return;
            string parent = Path.GetDirectoryName(projectPath).Replace('\\', '/');
            string leaf = Path.GetFileName(projectPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static bool ContainsAny(string s, params string[] tokens)
        {
            foreach (var t in tokens)
                if (s.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static Bounds ComputeBounds(GameObject go)
        {
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }

        /// <summary>활성 씬의 모든(비활성 포함) GameObject 중 이름 일치 첫 오브젝트 검색.</summary>
        private static GameObject FindInScene(string name)
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
                var t = FindDeep(root.transform, name);
                if (t != null) return t.gameObject;
            }
            return null;
        }

        private static Transform FindDeep(Transform t, string name)
        {
            foreach (Transform c in t)
            {
                if (c.name == name) return c;
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
