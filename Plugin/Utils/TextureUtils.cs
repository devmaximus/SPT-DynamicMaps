using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DynamicMaps.Utils
{
    public static class TextureUtils
    {
        private static Dictionary<string, Sprite> _spriteCache = new();

        private static Texture2D LoadTexture2DFromPath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(absolutePath));
            return tex;
        }

        public static Sprite GetOrLoadCachedSprite(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            if (_spriteCache.TryGetValue(path, out var sprite))
                return sprite;

            var absolutePath = Path.Combine(Plugin.Path, path);
            var texture = LoadTexture2DFromPath(absolutePath);
            if (texture == null)
                return null;

            // Pivot MUST be normalized 0-1. Passing pixel coords (w/2,h/2) makes
            // small custom markers vanish inside Unity UI Images.
            _spriteCache[path] = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);

            return _spriteCache[path];
        }

        /// <summary>
        /// Cache-backed procedural white sprite (opaque pixels only). Used for gear
        /// silhouettes so markers never depend on a missing/broken PNG.
        /// </summary>
        public static Sprite GetOrCreateCachedSprite(string cacheKey, int width, int height, System.Action<Texture2D> paint)
        {
            if (string.IsNullOrEmpty(cacheKey) || width < 1 || height < 1 || paint == null)
                return null;

            if (_spriteCache.TryGetValue(cacheKey, out var sprite))
                return sprite;

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;

            var clear = new Color32(0, 0, 0, 0);
            var pixels = new Color32[width * height];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = clear;
            }
            texture.SetPixels32(pixels);
            paint(texture);
            texture.Apply(false, false);

            _spriteCache[cacheKey] = Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                new Vector2(0.5f, 0.5f),
                100f);

            return _spriteCache[cacheKey];
        }

        public static void FillRect(Texture2D tex, int x0, int y0, int x1, int y1, Color32 color)
        {
            var maxX = Mathf.Min(x1, tex.width - 1);
            var maxY = Mathf.Min(y1, tex.height - 1);
            for (var y = Mathf.Max(0, y0); y <= maxY; y++)
            {
                for (var x = Mathf.Max(0, x0); x <= maxX; x++)
                {
                    tex.SetPixel(x, y, color);
                }
            }
        }
    }
}
