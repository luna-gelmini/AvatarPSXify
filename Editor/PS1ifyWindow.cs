using System.Linq;
using QuestKit;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace PS1ify
{
    public class PS1ifyWindow : EditorWindow
    {
        GameObject avatar;
        string outputName = "";
        int triTarget = 19000;
        float facetStrength = 1f;
        GUIStyle small;

        [MenuItem("Tools/PS1-ify")]
        static void Open()
        {
            GetWindow<PS1ifyWindow>("PS1-ify").minSize = new Vector2(380, 230);
        }

        void OnGUI()
        {
            if (small == null) small = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            EditorGUILayout.Space(4);
            var a = (GameObject)EditorGUILayout.ObjectField("Avatar", avatar, typeof(GameObject), true);
            if (a != avatar)
            {
                avatar = a;
                if (avatar != null) outputName = avatar.name + "_PS1";
            }
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Pick a PC avatar. It is never changed: Build makes a Quest copy and a PC copy in the PS1 style.", MessageType.Info);
                return;
            }
            if (avatar.GetComponent<VRCAvatarDescriptor>() == null)
            {
                EditorGUILayout.HelpBox("This object has no VRC Avatar Descriptor.", MessageType.Warning);
                return;
            }

            outputName = EditorGUILayout.TextField("Output name", outputName);
            triTarget = EditorGUILayout.IntSlider("Triangle target", triTarget, 5000, 70000);
            GUILayout.Label("The face and its shape keys are never reduced, so some avatars stop above the target.", small);
            facetStrength = EditorGUILayout.Slider("Facet strength", facetStrength, 0f, 1f);
            GUILayout.Label("Builds " + outputName + " (Quest, 256 per material) and " + outputName + "_PC (PC, twice that). Upload both to the same blueprint ID.", small);

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Build", GUILayout.Height(28))) Build();
        }

        void Build()
        {
            var plan = QuestPlan.Analyze(avatar);
            plan.outputName = outputName;

            // PS1 look on top of the Quest plan: everything toon goes through Toon Standard so the
            // facets can be baked, reduction is chunkier, and textures are replaced by PSX.Apply.
            foreach (var e in plan.materials)
            {
                if (e.userSet) continue;
                if (e.mode == MatMode.ToonStandardOutline || e.mode == MatMode.ToonLit || (e.mode == MatMode.Keep && e.src.shader.name.Contains("Toon")))
                    e.mode = MatMode.ToonStandard;
            }
            plan.reduce = true;
            plan.triTarget = Mathf.Min(plan.totalTris, triTarget);
            plan.reduceTolerance = 0.012f;
            plan.ComputeRatios();
            plan.atlas = true;
            plan.bakeGlowIntoAtlas = true;
            plan.setTextureSizes = false;
            plan.FitTextures();

            var dialog = "Builds \"" + outputName + "\" and \"" + outputName + "_PC\". " + avatar.name + " stays untouched.";
            var merges = plan.merges.Where(g => g.enabled).ToList();
            if (merges.Count > 0)
                dialog += "\n\nMeshes to merge:\n" + string.Join("\n", merges.Select(g => "• " + string.Join(" + ", g.members.Select(m => m.name).ToArray())).ToArray());
            if (!EditorUtility.DisplayDialog("PS1-ify", dialog, "Build", "Cancel")) return;

            GameObject quest;
            try
            {
                quest = QuestBuilder.Build(plan);
                EditorUtility.DisplayProgressBar("PS1-ify", "Baking facets and dithering textures", 0.9f);
                var folder = "Assets/QuestKit/Generated/" + QuestBuilder.Safe(avatar.name);
                var pcMats = PSX.Apply(quest, folder, facetStrength);
                MakePcTwin(quest, pcMats);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Selection.activeGameObject = quest;
            ShowNotification(new GUIContent("Built " + quest.name + " (" + plan.lastReduction + " triangles)"));
        }

        // Same meshes and setup as the Quest copy, with the double-resolution textures.
        static void MakePcTwin(GameObject quest, System.Collections.Generic.Dictionary<Material, Material> pcMats)
        {
            var pcName = quest.name + "_PC";
            var old = quest.scene.GetRootGameObjects().FirstOrDefault(g => g.name == pcName);
            var pos = old != null ? old.transform.position : quest.transform.position + quest.transform.right * 1.5f;
            if (old != null) Undo.DestroyObjectImmediate(old);
            var pc = Instantiate(quest, pos, quest.transform.rotation);
            pc.name = pcName;
            Undo.RegisterCreatedObjectUndo(pc, "PS1-ify PC avatar");
            foreach (var r in pc.GetComponentsInChildren<Renderer>(true))
                r.sharedMaterials = r.sharedMaterials.Select(m => m != null && pcMats.ContainsKey(m) ? pcMats[m] : m).ToArray();
        }
    }
}
