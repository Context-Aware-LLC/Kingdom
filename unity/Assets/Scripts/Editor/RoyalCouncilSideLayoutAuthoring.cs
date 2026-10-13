using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    // Applies Sam's compact side furniture to a fresh, fitted v06 instance. The source GLB
    // remains untouched; generated meshes keep their GUIDs when the prefab is rebuilt.
    internal static class RoyalCouncilSideLayoutAuthoring
    {
        private const float SurfaceHalf = 7.42f;
        private const float EndTurnPlatformLift = .15f;
        private const float HeroOutwardOffset = .30f;
        private const float WeaponPlatformLift = .40f;
        private const float WeaponVerticalOffset = 1f;
        private const string MeshRoot = "Assets/Art/Boards/RoyalCouncil/";

        internal static void Apply(GameObject model)
        {
            Transform root = model.transform.parent;
            Transform[] parts = model.GetComponentsInChildren<Transform>(true);
            Transform Part(string name) => Array.Find(parts, t => t.name == name);
            Vector3 stampPivot = new Vector3(8.80f, -.30f, Part("RC.Mount.EndTurn.Stamper").position.z);
            Vector3 waxPivot = new Vector3(8.80f, -1.85f, Part("RC.Mount.EndTurn.Seal").position.z);
            var weaponPivots = new Vector3[2];
            var weaponTargets = new Vector3[2];
            // Outer radii of the v06 hero/weapon frames, with a small clear gap.
            float weaponSeparation = 1.548f + .618f * Kingdom.Presentation.RoyalCouncilBoard.WeaponScale + .12f;
            float weaponHorizontalOffset = Mathf.Sqrt(weaponSeparation * weaponSeparation - WeaponVerticalOffset * WeaponVerticalOffset);
            for (int seat = 0; seat < 2; seat++)
            {
                float sign = seat == 0 ? -1f : 1f;
                weaponPivots[seat] = Part($"RC.Mount.P{seat}.Weapon").position;
                Vector3 hero = Part($"RC.Mount.P{seat}.Hero").position + Vector3.right * (sign * HeroOutwardOffset);
                // Sit beside the portrait: lower-right for us, upper-left for the opponent.
                // Keep the enlarged platform's base at the table height.
                weaponTargets[seat] = hero + new Vector3(-sign * weaponHorizontalOffset, sign * WeaponVerticalOffset, 0f);
                weaponTargets[seat].z = weaponPivots[seat].z - WeaponPlatformLift;
            }
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                string file = filter.name switch
                {
                    "RC.Static.Wood.Rail" => "TimerPlatformRail.asset",
                    "RC.Static.Base.Liner" => "ControlPlatformLiner.asset",
                    "RC.Static.Gold.Highlight" => "ControlPlatformBrass.asset",
                    "RC.Static.Bronze.Dark" => "SidePortraitBronze.asset",
                    "RC.Static.Wood.Seam" => "WideSurfaceSeams.asset",
                    "RC.Surface.Replaceable" => "WidePlaySurface.asset",
                    _ => null
                };
                if (file != null) RefitMesh(filter, MeshRoot + file, stampPivot, waxPivot, weaponPivots, weaponTargets);
            }

            Material wood = Part("RC.Static.Wood.Rail").GetComponent<Renderer>().sharedMaterial;
            Material dark = Part("RC.Static.Bronze.Dark").GetComponent<Renderer>().sharedMaterial;
            for (int seat = 0; seat < 2; seat++)
            {
                float heroSign = seat == 0 ? -1f : 1f;
                Part($"RC.Mount.P{seat}.Hero").position += Vector3.right * (heroSign * HeroOutwardOffset);
                Part($"RC.Mount.P{seat}.Weapon").position = weaponTargets[seat];
                AddBevel(root, wood, heroSign, seat);
                StandDeck(root, seat, Part($"RC.P{seat}.Pile.Card"), Part($"RC.P{seat}.Pile.Top"));
                AddSlot(root, seat, -heroSign * 8.73f, heroSign * 3.95f, wood, dark);
            }

            Transform stamp = Part("RC.Prop.Stamper"), stampMount = Part("RC.Mount.EndTurn.Stamper");
            Vector3 before = stampMount.position, after = stampPivot;
            Quaternion stampTilt = Kingdom.Presentation.RoyalCouncilSeal.RestRotation;
            Vector3 controlLift = Vector3.back * EndTurnPlatformLift;
            stamp.position = after + stampTilt * (stamp.position - before) * .85f + controlLift;
            stamp.rotation = stampTilt * stamp.rotation;
            stamp.localScale *= .85f;
            stampMount.position = after + controlLift;
            stampMount.rotation = stampTilt * stampMount.rotation;
            Transform wax = Part("RC.Prop.WaxSeal"), waxMount = Part("RC.Mount.EndTurn.Seal");
            // Rotate the seal and every layer of its support around the same contact point.
            wax.position = waxPivot + stampTilt * (wax.position - waxMount.position) + controlLift;
            wax.rotation = stampTilt * wax.rotation;
            waxMount.position = waxPivot + controlLift;
            waxMount.rotation = stampTilt * waxMount.rotation;
            foreach (Transform part in parts)
                if (part.name == "RC.Mount.Timer" || part.name.StartsWith("RC.Prop.Hourglass", StringComparison.Ordinal))
                    part.position += Vector3.left * .36f;
        }

        private static bool Near(float a, float b) => Mathf.Abs(a - b) < .025f;

        private static void RefitMesh(MeshFilter filter, string path, Vector3 stampPivot, Vector3 waxPivot,
            Vector3[] weaponPivots, Vector3[] weaponTargets)
        {
            Mesh mesh = UnityEngine.Object.Instantiate(filter.sharedMesh);
            mesh.name = "Royal Council compact sides " + filter.name;
            Vector3[] vertices = mesh.vertices, normals = mesh.normals;
            var remove = new HashSet<int>();
            foreach (int[] indices in Islands(mesh))
            {
                Bounds bounds = new Bounds(filter.transform.TransformPoint(vertices[indices[0]]), Vector3.zero);
                foreach (int i in indices) bounds.Encapsulate(filter.transform.TransformPoint(vertices[i]));
                Vector3 center = bounds.center, size = bounds.size;
                float sign = Mathf.Sign(center.x);
                bool rail = filter.name == "RC.Static.Wood.Rail";
                bool gold = filter.name == "RC.Static.Gold.Highlight";
                bool liner = filter.name == "RC.Static.Base.Liner";
                bool removePart = (rail && Near(Mathf.Abs(center.x), 6.792f))
                    || (rail && Near(Mathf.Abs(center.x), 10.20f) && Near(Mathf.Abs(center.y), 5.58f)
                        && center.x * center.y < 0f && Near(size.x, .90f))
                    || (rail && (Near(Mathf.Abs(center.x), 7.224f) || Near(Mathf.Abs(center.x), 9.786f)) && Near(Mathf.Abs(center.y), 3.711f))
                    || (rail && Near(Mathf.Abs(center.x), 8.58f) && Near(Mathf.Abs(center.y), 2.52f))
                    || (liner && Near(size.x, 2.736f) && Near(size.y, 2.94f))
                    || (gold && Near(Mathf.Abs(center.x), 7.032f) && size.y > 10f)
                    || (gold && Near(Mathf.Abs(center.x), 6.69f) && Near(size.y, .66f));
                if (removePart) { foreach (int i in indices) remove.Add(i); continue; }

                Vector3 shift = Vector3.zero;
                float scale = 1f;
                bool manaRail = rail && Near(Mathf.Abs(center.x), 10.356f) && Near(size.x, .852f)
                    && size.y > 7f && size.y < 8f;
                float manaEndSign = -sign;
                float manaEnd = sign > 0f ? -bounds.min.y : bounds.max.y;
                bool widen = filter.name == "RC.Surface.Replaceable" || filter.name == "RC.Static.Wood.Seam"
                    || (gold && size.x > 13f && Near(Mathf.Abs(center.y), 4.2f));
                bool hero = Mathf.Abs(center.x) > 7.1f && Mathf.Abs(center.x) < 10.3f && center.x * center.y > 0f
                    && Vector2.Distance(new Vector2(center.x, center.y), new Vector2(sign * 8.7f, sign * 3.84f)) < 1.58f
                    && size.x < 3.2f && size.y < 3.2f;
                if (hero) shift = Vector3.right * (sign * HeroOutwardOffset);
                int weaponSeat = Array.FindIndex(weaponPivots, p => Near(center.x, p.x) && Near(center.y, p.y));
                bool stampPlatform = Near(center.x, 7.56f) && Near(center.y, 0f) && size.x < 1.5f;
                if (stampPlatform)
                { shift = new Vector3(8.80f - 7.56f, -.30f, 0f); scale = .85f; }
                bool waxPlatform = Near(center.x, 9.12f) && Near(center.y, 0f) && size.x < 1.6f;
                if (waxPlatform)
                    shift = new Vector3(8.80f - 9.12f, -1.85f, 0f);
                bool timer = Near(center.x, -8.34f) && Near(center.y, 0f) && size.x < 2f;
                if (timer) shift = Vector3.left * .36f;
                Quaternion tilt = Kingdom.Presentation.RoyalCouncilHourglass.RestRotation;
                Vector3 pivot = new Vector3(-8.70f, 0f, -.18f);
                foreach (int i in indices)
                {
                    Vector3 world = filter.transform.TransformPoint(vertices[i]);
                    Vector3 normal = filter.transform.TransformDirection(normals[i]);
                    // Carry the existing timber into the corner after removing its raised
                    // block. Translate the whole end bevel so the rail keeps its profile.
                    if (manaRail && manaEndSign * world.y > manaEnd - .10f)
                        world.y += manaEndSign * (6f - manaEnd);
                    if (widen) { world.x *= SurfaceHalf / 6.69f; normal.x /= SurfaceHalf / 6.69f; }
                    if (scale != 1f)
                    {
                        world.x = center.x + (world.x - center.x) * scale;
                        world.y = center.y + (world.y - center.y) * scale;
                        normal.x /= scale; normal.y /= scale;
                    }
                    world += shift;
                    if (weaponSeat >= 0)
                        world = weaponTargets[weaponSeat] + (world - weaponPivots[weaponSeat]) * Kingdom.Presentation.RoyalCouncilBoard.WeaponScale;
                    if (timer) { world = pivot + tilt * (world - pivot); normal = tilt * normal; }
                    if (stampPlatform || waxPlatform)
                    {
                        Quaternion stampTilt = Kingdom.Presentation.RoyalCouncilSeal.RestRotation;
                        Vector3 controlPivot = stampPlatform ? stampPivot : waxPivot;
                        // Keep the inclined rear rim above the purple panel. Move prop, mount
                        // and every support layer together so their contact plane stays intact.
                        world = controlPivot + stampTilt * (world - controlPivot) + Vector3.back * EndTurnPlatformLift;
                        normal = stampTilt * normal;
                    }
                    vertices[i] = filter.transform.InverseTransformPoint(world);
                    normals[i] = filter.transform.InverseTransformDirection(normal.normalized);
                }
            }
            int[] triangles = mesh.triangles;
            var keep = new List<int>(triangles.Length);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                bool a = remove.Contains(triangles[i]), b = remove.Contains(triangles[i + 1]), c = remove.Contains(triangles[i + 2]);
                if (a != b || a != c) throw new InvalidOperationException(filter.name + ": side furniture removal cuts a triangle.");
                if (!a) { keep.Add(triangles[i]); keep.Add(triangles[i + 1]); keep.Add(triangles[i + 2]); }
            }
            mesh.vertices = vertices; mesh.normals = normals; mesh.triangles = keep.ToArray(); mesh.RecalculateBounds();
            filter.sharedMesh = SaveMesh(mesh, path);
        }

        // The GLB batches many separate fixtures by material. Weld hard-edge duplicates only
        // to identify complete islands; selecting whole islands cannot slice neighbouring trim.
        private static IEnumerable<int[]> Islands(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            int[] parents = Enumerable.Range(0, vertices.Length).ToArray();
            int Find(int i) { while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; } return i; }
            void Join(int a, int b) { parents[Find(a)] = Find(b); }
            var welded = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3Int key = Vector3Int.RoundToInt(vertices[i] * 10000f);
                if (welded.TryGetValue(key, out int previous)) Join(i, previous); else welded[key] = i;
            }
            int[] triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3) { Join(triangles[i], triangles[i + 1]); Join(triangles[i], triangles[i + 2]); }
            return Enumerable.Range(0, vertices.Length).GroupBy(Find).Select(g => g.ToArray()).ToArray();
        }

        private static Mesh SaveMesh(Mesh mesh, string path)
        {
            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null) { AssetDatabase.CreateAsset(mesh, path); return mesh; }
            EditorUtility.CopySerialized(mesh, existing);
            existing.vertices = mesh.vertices; existing.normals = mesh.normals;
            existing.triangles = mesh.triangles; existing.RecalculateBounds();
            UnityEngine.Object.DestroyImmediate(mesh);
            return existing;
        }

        private static void AddBevel(Transform root, Material material, float sign, int seat)
        {
            var mesh = new Mesh { name = "Inset wood to fabric bevel" };
            float inner = SurfaceHalf - .04f, outer = SurfaceHalf + .18f, y = 4.29f;
            mesh.vertices = new[] { new Vector3(sign * inner, -y, .012f), new Vector3(sign * inner, y, .012f),
                new Vector3(sign * outer, y, .226f), new Vector3(sign * outer, -y, .226f) };
            mesh.triangles = sign > 0 ? new[] { 0, 1, 2, 0, 2, 3 } : new[] { 0, 2, 1, 0, 3, 2 };
            mesh.uv = mesh.vertices.Select(v => new Vector2(v.y / 2f, v.x / 2f)).ToArray();
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            AddMesh(root, $"RC.P{seat}.InsetBevel", mesh, material, $"InsetBevel{seat}.asset");
        }

        private static void StandDeck(Transform root, int seat, Transform card, Transform top)
        {
            var rack = new GameObject($"RC.P{seat}.DeckRack").transform;
            rack.SetParent(root, false); rack.position = card.GetComponent<Renderer>().bounds.center;
            card.SetParent(rack, true); top.SetParent(rack, true);
            rack.rotation = Quaternion.AngleAxis(-82f, Vector3.right) * Quaternion.AngleAxis(90f, Vector3.forward);
            rack.localScale = new Vector3(.65f, .90f, 1f);
            Bounds bounds = card.GetComponent<Renderer>().bounds;
            bounds.Encapsulate(top.GetComponent<Renderer>().bounds);
            Bounds last = bounds;
            last.center += card.up.normalized * (.024f * 29f);
            bounds.Encapsulate(last);
            float sign = seat == 0 ? 1f : -1f;
            rack.position += new Vector3(sign * 8.73f, -sign * 3.95f, .228f) - bounds.center;
        }

        private static void AddSlot(Transform root, int seat, float x, float y, Material wood, Material dark)
        {
            Vector3[] outer = { new Vector3(x - 1.22f, y - .55f, .16f), new Vector3(x + 1.22f, y - .55f, .16f),
                new Vector3(x + 1.22f, y + .55f, .16f), new Vector3(x - 1.22f, y + .55f, .16f) };
            Vector3[] inner = { new Vector3(x - 1.10f, y - .41f, .31f), new Vector3(x + 1.10f, y - .41f, .31f),
                new Vector3(x + 1.10f, y + .41f, .31f), new Vector3(x - 1.10f, y + .41f, .31f) };
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4, n = vertices.Count;
                vertices.AddRange(new[] { outer[i], inner[i], inner[j], outer[j] });
                triangles.AddRange(new[] { n, n + 1, n + 2, n, n + 2, n + 3 });
            }
            var mesh = new Mesh { name = "Recessed deck slot rim", vertices = vertices.ToArray(), triangles = triangles.ToArray() };
            mesh.uv = vertices.Select(v => new Vector2(v.y / 2f, v.x / 2f)).ToArray(); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            AddMesh(root, $"RC.P{seat}.DeckSlot.Rim", mesh, wood, $"DeckSlotRim{seat}.asset");
            var floor = new Mesh { name = "Recessed deck slot base", vertices = inner.Select(v => v + Vector3.forward * .015f).ToArray(), triangles = new[] { 0, 2, 1, 0, 3, 2 } };
            floor.RecalculateNormals(); floor.RecalculateBounds();
            AddMesh(root, $"RC.P{seat}.DeckSlot.Base", floor, dark, $"DeckSlotBase{seat}.asset");
        }

        private static void AddMesh(Transform parent, string name, Mesh mesh, Material material, string file)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = SaveMesh(mesh, MeshRoot + file);
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
    }
}
