using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Carries the look of a material on another shader (Standard, URP Lit, lilToon, Poiyomi, UTS, ...) over to a
    /// Blend Property shader: base colour and texture, normal map, smoothness, metallic, emission. Each slot takes the
    /// first property name the source has.
    /// </summary>
    public static class MaterialTransfer
    {
        // ponytail: name lists cover the common Built-in / URP / VRChat toon shaders; add names here for others
        static readonly string[] Colors = { "_Color", "_BaseColor", "_MainColor" };
        static readonly string[] MainTex = { "_MainTex", "_BaseMap", "_BaseColorMap", "_MainTexture" };
        static readonly string[] Normal = { "_BumpMap", "_NormalMap", "_MainNormal" };
        static readonly string[] NormalScale = { "_BumpScale", "_NormalScale", "_MainNormalScale" };
        static readonly string[] Smooth = { "_Glossiness", "_Smoothness" };
        static readonly string[] Metal = { "_Metallic" };
        static readonly string[] Emission = { "_EmissionColor" };
        static readonly string[] EmissionTex = { "_EmissionMap", "_EmissionTex" };

        public static void Copy(Material src, Material dst)
        {
            if (src == null || dst == null) return;
            CopyColor(src, Colors, dst, "_Color");
            CopyTexture(src, MainTex, dst, "_MainTex", true);
            CopyTexture(src, Normal, dst, "_BumpMap", false);
            CopyFloat(src, NormalScale, dst, "_BumpScale");
            CopyFloat(src, Smooth, dst, "_Glossiness");
            CopyFloat(src, Metal, dst, "_Metallic");
            bool emissionOff = src.shader.name.StartsWith("Standard") && !src.IsKeywordEnabled("_EMISSION"); // Standard keeps the colour when off
            if (emissionOff) return;
            CopyColor(src, Emission, dst, "_EmissionColor");
            CopyTexture(src, EmissionTex, dst, "_EmissionMap", false);
        }

        static string First(Material m, string[] names)
        {
            foreach (var n in names) if (m.HasProperty(n)) return n;
            return null;
        }

        static void CopyColor(Material src, string[] names, Material dst, string to)
        {
            var n = First(src, names);
            if (n != null && dst.HasProperty(to)) dst.SetColor(to, src.GetColor(n));
        }

        static void CopyFloat(Material src, string[] names, Material dst, string to)
        {
            var n = First(src, names);
            if (n != null && dst.HasProperty(to)) dst.SetFloat(to, src.GetFloat(n));
        }

        static void CopyTexture(Material src, string[] names, Material dst, string to, bool tiling)
        {
            var n = First(src, names);
            if (n == null || !dst.HasProperty(to) || src.GetTexture(n) == null) return;
            dst.SetTexture(to, src.GetTexture(n));
            if (!tiling) return;
            dst.SetTextureScale(to, src.GetTextureScale(n));
            dst.SetTextureOffset(to, src.GetTextureOffset(n));
        }
    }
}
