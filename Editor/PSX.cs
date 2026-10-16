using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using QuestKit;
using UnityEngine;

namespace PS1ify
{
    // "Cute PS1" look: flat-shaded facets baked into small, dithered, point-filtered textures.
    // The face (anything a shape key moves) stays smooth and unshaded so expressions read well.
    public static class PSX
    {
        // Base sizes for a texture holding one material; atlases grow with how many they hold.
        public const int QuestSize = 256, PcSize = 512;
        static readonly Vector3 Light = new Vector3(0.35f, 0.65f, 0.65f).normalized;
        const float Dark = 0.68f, Bright = 1.06f;
        // Shadowed facets lean warm and pink like an anime shadow, instead of going grey or olive.
        static readonly Color ShadowTint = new Color(0.76f, 0.64f, 0.72f);

        static readonly float[] Bayer =
        {
            0, 8, 2, 10,
            12, 4, 14, 6,
            3, 11, 1, 9,
            15, 7, 13, 5,
        };

        // Bakes facets into every Toon Lit material, flat-shades the meshes, and returns the PC
        // version of each material (same look at twice the resolution).
        public static Dictionary<Material, Material> Apply(GameObject avatar, string folder, float facetStrength)
        {
            var renderers = avatar.GetComponentsInChildren<Renderer>(true).Where(r => !(r is ParticleSystemRenderer)).ToList();
            var byMaterial = new Dictionary<Material, List<KeyValuePair<Renderer, int>>>();
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                for (int s = 0; s < mats.Length; s++)
                {
                    if (mats[s] == null) continue;
                    List<KeyValuePair<Renderer, int>> list;
                    if (!byMaterial.TryGetValue(mats[s], out list)) byMaterial[mats[s]] = list = new List<KeyValuePair<Renderer, int>>();
                    list.Add(new KeyValuePair<Renderer, int>(r, s));
                }
            }

            var eyes = new HashSet<Transform>();
            var animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                foreach (var hb in new[] { HumanBodyBones.LeftEye, HumanBodyBones.RightEye })
                {
                    var t = animator.GetBoneTransform(hb);
                    if (t != null) foreach (var x in t.GetComponentsInChildren<Transform>(true)) eyes.Add(x);
                }

            var pcMats = new Dictionary<Material, Material>();
            foreach (var kv in byMaterial)
            {
                var m = kv.Key;
                var tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                if (tex == null) continue;
                bool lit = m.shader.name.StartsWith("VRChat/Mobile/Toon");
                int packed = Mathf.Max(1, AtlasCount(m));
                int questSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.RoundToInt(QuestSize * Mathf.Sqrt(packed))), QuestSize, 1024);
                // Faces are what people look at; they get twice the resolution.
                bool face = FaceShare(kv.Value) > 0.3f;
                if (face) questSize = Mathf.Min(1024, questSize * 2);
                int pcSize = Mathf.Min(2048, questSize * 2);
                int w, h;
                var src = TextureUtil.Pixels(tex, 2048, out w, out h);
                // Faces get no facets at all: half-faceted faces look blotchy and skull-like.
                if (lit && !face) BakeFacets(src, w, h, kv.Value, facetStrength, eyes);

                var questTex = Write(src, w, h, questSize, folder + "/" + QuestBuilder.Safe(m.name) + "_PS1.png");
                var pcTex = Write(src, w, h, pcSize, folder + "/" + QuestBuilder.Safe(m.name) + "_PS1_PC.png");
                m.SetTexture("_MainTex", questTex);
                if (lit && m.HasProperty("_Ramp"))
                {
                    m.SetFloat("_GlossStrength", 0f);
                    // Faces get the softest ramp: no hard shadow band, but not flat-lit and pale either.
                    if (face)
                    {
                        var soft = Resources.Load<Texture>("VRChat/ShadowRampRealisticVerySoft");
                        if (soft != null) m.SetTexture("_Ramp", soft);
                    }
                    m.DisableKeyword("USE_SPECULAR");
                    m.DisableKeyword("USE_NORMAL_MAPS");
                    if (m.HasProperty("_EmissionMap") && m.GetTexture("_EmissionMap") != null) m.SetTexture("_EmissionMap", questTex);
                }
                EditorUtility.SetDirty(m);

                var pc = new Material(m) { name = m.name + "_PC" };
                pc.SetTexture("_MainTex", pcTex);
                var path = folder + "/" + QuestBuilder.Safe(pc.name) + ".mat";
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(pc, path);
                pcMats[m] = pc;
            }

            // Facets live in the texture. Flat normals on top would drop half of every surface into
            // the shader's shadow band and turn colours grey, so normals stay smooth.
            return pcMats;
        }

        // Darkens each texel by how its triangle faces a fixed light, so facets show under any lighting.
        static void BakeFacets(Color[] px, int w, int h, List<KeyValuePair<Renderer, int>> users, float strength, HashSet<Transform> eyes)
        {
            var sum = new float[w * h];
            var count = new int[w * h];
            foreach (var u in users)
            {
                var mesh = MeshOf(u.Key);
                if (mesh == null || u.Value >= mesh.subMeshCount || mesh.GetTopology(u.Value) != MeshTopology.Triangles) continue;
                var verts = mesh.vertices;
                var uv = mesh.uv;
                if (uv.Length != verts.Length) continue;
                var moving = ShapeMoved(mesh);
                var smr = u.Key as SkinnedMeshRenderer;
                var bones = smr != null ? smr.bones : null;
                var bw = smr != null ? mesh.boneWeights : null;
                var toWorld = u.Key.transform.localToWorldMatrix;
                var idx = mesh.GetIndices(u.Value);
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                    float shade = 1f;
                    // Eyes keep their sparkle: anything that follows an eye bone stays unshaded.
                    bool eye = bw != null && bw.Length > a && bw[a].boneIndex0 < bones.Length && eyes.Contains(bones[bw[a].boneIndex0]);
                    if (!eye && !moving[a] && !moving[b] && !moving[c])
                    {
                        var n = Vector3.Cross(toWorld.MultiplyPoint3x4(verts[b]) - toWorld.MultiplyPoint3x4(verts[a]),
                                              toWorld.MultiplyPoint3x4(verts[c]) - toWorld.MultiplyPoint3x4(verts[a])).normalized;
                        float t = Vector3.Dot(n, Light) * 0.5f + 0.5f;
                        shade = Mathf.Lerp(1f, Mathf.Lerp(Dark, Bright, t), strength);
                    }
                    Raster(uv[a], uv[b], uv[c], w, h, shade, sum, count);
                }
            }
            // Fill a couple of texels past triangle edges so point sampling never hits an unshaded gap.
            for (int pass = 0; pass < 2; pass++)
            {
                var add = new List<KeyValuePair<int, float>>();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (count[i] > 0) continue;
                        float s = 0; int c = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int j = ny * w + nx;
                                if (count[j] > 0) { s += sum[j] / count[j]; c++; }
                            }
                        if (c > 0) add.Add(new KeyValuePair<int, float>(i, s / c));
                    }
                foreach (var kv in add) { sum[kv.Key] = kv.Value; count[kv.Key] = 1; }
            }
            for (int i = 0; i < px.Length; i++)
            {
                if (count[i] == 0) continue;
                float s = sum[i] / count[i];
                Color mul;
                if (s >= 1f) mul = new Color(s, s, s);
                else
                {
                    float k = Mathf.InverseLerp(Dark, 1f, s);
                    var tint = Color.Lerp(Color.white, ShadowTint, Mathf.Clamp01((1f - Dark) > 0 ? (1f - s) / (1f - Dark) : 0));
                    mul = new Color(tint.r, tint.g, tint.b) * Mathf.Lerp(0.92f, 1f, k);
                }
                px[i] = new Color(Mathf.Clamp01(px[i].r * mul.r), Mathf.Clamp01(px[i].g * mul.g), Mathf.Clamp01(px[i].b * mul.b), px[i].a);
            }
        }

        static void Raster(Vector2 a, Vector2 b, Vector2 c, int w, int h, float shade, float[] sum, int[] count)
        {
            a = Wrap(a); b = Wrap(b); c = Wrap(c);
            Vector2 pa = new Vector2(a.x * w, a.y * h), pb = new Vector2(b.x * w, b.y * h), pc = new Vector2(c.x * w, c.y * h);
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.x, pb.x, pc.x))), x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(pa.x, pb.x, pc.x)));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.y, pb.y, pc.y))), y1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(pa.y, pb.y, pc.y)));
            float area = Edge(pa, pb, pc);
            if (Mathf.Abs(area) < 1e-6f) return;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(pb, pc, p) / area, w1 = Edge(pc, pa, p) / area, w2 = Edge(pa, pb, p) / area;
                    if (w0 < -0.02f || w1 < -0.02f || w2 < -0.02f) continue;
                    sum[y * w + x] += shade;
                    count[y * w + x]++;
                }
        }

        static Vector2 Wrap(Vector2 v)
        {
            return v.x >= -0.01f && v.x <= 1.01f && v.y >= -0.01f && v.y <= 1.01f ? v : new Vector2(v.x - Mathf.Floor(v.x), v.y - Mathf.Floor(v.y));
        }

        static float Edge(Vector2 a, Vector2 b, Vector2 p)
        {
            return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        }

        // Box-downscale to size, then 15-bit colour with a 4x4 Bayer dither, saved point-filtered.
        static Texture2D Write(Color[] src, int w, int h, int size, string path)
        {
            float s = Mathf.Min(1f, size / (float)Mathf.Max(w, h));
            int tw = Mathf.Max(1, Mathf.RoundToInt(w * s)), th = Mathf.Max(1, Mathf.RoundToInt(h * s));
            var px = new Color[tw * th];
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int sx0 = x * w / tw, sx1 = Mathf.Max(sx0 + 1, (x + 1) * w / tw);
                    int sy0 = y * h / th, sy1 = Mathf.Max(sy0 + 1, (y + 1) * h / th);
                    Color acc = Color.clear;
                    for (int yy = sy0; yy < sy1; yy++) for (int xx = sx0; xx < sx1; xx++) acc += src[yy * w + xx];
                    acc /= (sx1 - sx0) * (sy1 - sy0);
                    float d = (Bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f;
                    px[y * tw + x] = new Color(Q5(acc.r, d), Q5(acc.g, d), Q5(acc.b, d), acc.a < 0.5f ? 0f : 1f);
                }
            var tex = TextureUtil.WritePng(px, tw, th, path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.filterMode = FilterMode.Point;
            ti.mipmapEnabled = false;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.maxTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(tw, th));
            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = ti.maxTextureSize;
            // ASTC 4x4 keeps the dither pattern readable at a quarter of the uncompressed size.
            android.format = TextureImporterFormat.ASTC_4x4;
            ti.SetPlatformTextureSettings(android);
            ti.SaveAndReimport();
            return tex;
        }

        static float Q5(float v, float dither)
        {
            return Mathf.Clamp(Mathf.Floor(v * 31f + dither), 0, 31) / 31f;
        }

        static float FaceShare(List<KeyValuePair<Renderer, int>> users)
        {
            int total = 0, face = 0;
            foreach (var u in users)
            {
                var mesh = MeshOf(u.Key);
                if (mesh == null || u.Value >= mesh.subMeshCount || mesh.GetTopology(u.Value) != MeshTopology.Triangles) continue;
                var moving = ShapeMoved(mesh);
                var idx = mesh.GetIndices(u.Value);
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    total++;
                    if (moving[idx[i]] || moving[idx[i + 1]] || moving[idx[i + 2]]) face++;
                }
            }
            return total > 0 ? face / (float)total : 0;
        }

        // Atlas materials record how many materials they hold in their name (see Atlas.Build).
        static int AtlasCount(Material m)
        {
            int i = m.name.LastIndexOf("_x");
            int n;
            return i >= 0 && int.TryParse(m.name.Substring(i + 2), out n) ? n : 1;
        }

        static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        static bool[] ShapeMoved(Mesh mesh)
        {
            var moved = new bool[mesh.vertexCount];
            var d = new Vector3[mesh.vertexCount];
            for (int s = 0; s < mesh.blendShapeCount; s++)
            {
                mesh.GetBlendShapeFrameVertices(s, mesh.GetBlendShapeFrameCount(s) - 1, d, null, null);
                for (int i = 0; i < d.Length; i++) if (d[i].sqrMagnitude > 1e-10f) moved[i] = true;
            }
            return moved;
        }
    }
}
