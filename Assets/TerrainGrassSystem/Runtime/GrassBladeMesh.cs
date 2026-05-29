using System.Collections.Generic;
using UnityEngine;

namespace TerrainGrassSystem
{
    // Builds the unit blade meshes used as instancing templates. The vertex
    // shader treats positionOS.x as the side (-1 left, 0 tip, +1 right) and
    // positionOS.y as the segment parameter v (0 root .. 1 tip).
    //
    // High LOD: N segments + tip. Vertex count = (segments * 2) + 1.
    // Low LOD: typically 1 or 2 segments + tip — cheap silhouette for distant blades.
    public static class GrassBladeMesh
    {
        public const int HighLodSegments = 4; // produces 9 verts, 7 tris
        public const int LowLodSegments  = 1; // produces 3 verts, 1 tri

        public static Mesh CreateHighLod()  => Build(HighLodSegments, "GrassBlade_HighLOD");
        public static Mesh CreateLowLod()   => Build(LowLodSegments,  "GrassBlade_LowLOD");

        public static Mesh Build(int segments, string name)
        {
            segments = Mathf.Max(1, segments);

            var verts = new List<Vector3>((segments * 2) + 1);
            var uvs   = new List<Vector2>((segments * 2) + 1);
            var idx   = new List<int>(segments * 6);

            // Lower rows: two verts per segment row.
            for (int s = 0; s < segments; ++s)
            {
                float v = s / (float)segments;
                verts.Add(new Vector3(-1f, v, 0f));
                verts.Add(new Vector3(+1f, v, 0f));
                uvs.Add(new Vector2(0f, v));
                uvs.Add(new Vector2(1f, v));
            }

            // Tip vertex.
            int tipIndex = verts.Count;
            verts.Add(new Vector3(0f, 1f, 0f));
            uvs.Add(new Vector2(0.5f, 1f));

            // Two triangles per non-tip segment, one triangle to the tip for the last.
            for (int s = 0; s < segments - 1; ++s)
            {
                int row0L = s * 2;
                int row0R = s * 2 + 1;
                int row1L = (s + 1) * 2;
                int row1R = (s + 1) * 2 + 1;

                idx.Add(row0L); idx.Add(row1L); idx.Add(row0R);
                idx.Add(row0R); idx.Add(row1L); idx.Add(row1R);
            }

            // Final segment terminates in the tip vertex.
            {
                int s = segments - 1;
                int row0L = s * 2;
                int row0R = s * 2 + 1;
                idx.Add(row0L); idx.Add(tipIndex); idx.Add(row0R);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(idx, 0);
            mesh.RecalculateBounds();
            // Large local bounds because real position is set in vertex shader.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 8f);
            mesh.UploadMeshData(false);
            return mesh;
        }

        public static int IndexCountFor(Mesh mesh)
        {
            return (int)mesh.GetIndexCount(0);
        }
    }
}
