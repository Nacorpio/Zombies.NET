using System.Buffers.Binary;
using System.IO.Compression;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

public sealed class RenderTests
{
    [Fact]
    public void Rgba_PacksBytesInRgbaMemoryOrder()
    {
        var packed = new Rgba(1, 2, 3, 4).Packed;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, BitConverter.GetBytes(packed));
        Assert.Equal(255, Rgba.White.A);
        Assert.Equal(7, Rgba.Black.WithAlpha(7).A);
    }

    [Fact]
    public void DebugFont_EveryDocumentedCharacterHasAGlyphAndUnknownOnesFallBackToSpace()
    {
        foreach (var c in " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.:-/%(),+=_|")
        {
            Assert.True(DebugFont.HasGlyph(c), $"missing glyph for '{c}'");
        }

        Assert.False(DebugFont.HasGlyph('~'));
        Assert.Equal(DebugFont.GlyphUv(' '), DebugFont.GlyphUv('~'));
        Assert.Equal(DebugFont.GlyphUv('A'), DebugFont.GlyphUv('a'));
    }

    [Fact]
    public void DebugFont_AtlasHasLitPixelsForLettersNoneForSpaceAndASolidCell()
    {
        var atlas = DebugFont.CreateAtlas();

        Assert.Equal(DebugFont.AtlasWidth * DebugFont.AtlasHeight, atlas.Length);
        Assert.True(CountLit(atlas, DebugFont.GlyphUv('A')) > 10);
        Assert.Equal(0, CountLit(atlas, DebugFont.GlyphUv(' ')));
        Assert.Equal(0, CountLit(atlas, DebugFont.GlyphUv('I'), onlyColumn: 4) + CountLit(atlas, DebugFont.GlyphUv('T'), onlyRow: 7));

        var solid = DebugFont.SolidUv();
        var pixels = (int)((solid.U1 - solid.U0) * DebugFont.AtlasWidth) * (int)((solid.V1 - solid.V0) * DebugFont.AtlasHeight);
        Assert.Equal(pixels, CountLit(atlas, solid));
    }

    [Fact]
    public void DebugFont_UvRectanglesStayInsideTheAtlas()
    {
        foreach (var uv in "0123456789AZ.|".Select(DebugFont.GlyphUv).Append(DebugFont.SolidUv()))
        {
            Assert.InRange(uv.U0, 0f, 1f);
            Assert.InRange(uv.U1, 0f, 1f);
            Assert.InRange(uv.V0, 0f, 1f);
            Assert.InRange(uv.V1, 0f, 1f);
            Assert.True(uv.U1 > uv.U0 && uv.V1 > uv.V0);
        }
    }

    [Fact]
    public void SpriteBatch_FillRectAddsOneQuadWithTheRightCorners()
    {
        var batch = new SpriteBatch();

        batch.FillRect(10, 20, 30, 40, new Rgba(9, 8, 7, 6));

        Assert.Equal(1, batch.QuadCount);
        var v = batch.Vertices;
        Assert.Equal(4, v.Length);
        Assert.Equal((10f, 20f), (v[0].X, v[0].Y));
        Assert.Equal((40f, 20f), (v[1].X, v[1].Y));
        Assert.Equal((40f, 60f), (v[2].X, v[2].Y));
        Assert.Equal((10f, 60f), (v[3].X, v[3].Y));
        Assert.All(v.ToArray(), vertex => Assert.Equal(new Rgba(9, 8, 7, 6).Packed, vertex.Color));
    }

    [Fact]
    public void SpriteBatch_TextSkipsSpacesAdvancesAndHandlesNewlines()
    {
        var batch = new SpriteBatch();

        batch.DrawText("A B\nC", 5, 5, 2, Rgba.White);

        Assert.Equal(3, batch.QuadCount);
        var v = batch.Vertices;
        Assert.Equal(5f, v[0].X);
        Assert.Equal(5f + (2 * DebugFont.Advance * 2), v[4].X);
        Assert.Equal(5f, v[8].X);
        Assert.Equal(5f + (DebugFont.LineHeight * 2), v[8].Y);
        Assert.Equal(3 * DebugFont.Advance * 3, DebugFont.MeasureWidth("ABC", 3));
    }

    [Fact]
    public void SpriteBatch_ClearResetsAndOverflowDropsQuadsInsteadOfThrowing()
    {
        var batch = new SpriteBatch();
        for (var i = 0; i < SpriteBatch.MaxQuads + 5; i++)
        {
            batch.FillRect(0, 0, 1, 1, Rgba.White);
        }

        Assert.Equal(SpriteBatch.MaxQuads, batch.QuadCount);
        Assert.True(batch.Overflowed);

        batch.Clear();

        Assert.Equal(0, batch.QuadCount);
        Assert.False(batch.Overflowed);
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.DrawText("A", 0, 0, 0, Rgba.White));
    }

    [Fact]
    public void FrameStats_AveragesOverTheRollingWindow()
    {
        var stats = new FrameStats(window: 4);
        Assert.Equal(0, stats.Fps);

        foreach (var ms in new[] { 10.0, 20.0, 30.0, 40.0 })
        {
            stats.Add(ms / 1000);
        }

        Assert.Equal(25, stats.AverageMilliseconds, 6);
        Assert.Equal(10, stats.MinMilliseconds, 6);
        Assert.Equal(40, stats.MaxMilliseconds, 6);
        Assert.Equal(40, stats.Fps, 6);

        stats.Add(0.050);

        Assert.Equal(4, stats.Samples);
        Assert.Equal(35, stats.AverageMilliseconds, 6);
        Assert.Equal(20, stats.MinMilliseconds, 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameStats(0));
    }

    [Fact]
    public void FrameStats_ToStringIsCultureIndependent()
    {
        var stats = new FrameStats();
        stats.Add(0.020);

        Assert.Equal("20.00 ms (50 fps)", stats.ToString());
    }

    [Fact]
    public void DebugOverlay_ShowsFrameTimeDeviceAndSizeAndColorsByBudget()
    {
        var stats = new FrameStats();
        stats.Add(0.008);

        var lines = DebugOverlay.Lines(stats, "My GPU", 1280, 720);

        Assert.Equal("FRAME 8.00 MS", lines[0]);
        Assert.Contains("125 FPS", lines[1], StringComparison.Ordinal);
        Assert.Equal("MY GPU", lines[2]);
        Assert.Equal("1280X720", lines[3]);
        Assert.NotEqual(DebugOverlay.ColorFor(10), DebugOverlay.ColorFor(25));
        Assert.NotEqual(DebugOverlay.ColorFor(25), DebugOverlay.ColorFor(50));
        Assert.True(lines.All(l => l.All(DebugFont.HasGlyph)), "overlay uses a character the font cannot draw");
    }

    [Fact]
    public void DebugOverlay_DrawsAPanelPlusOneQuadPerVisibleCharacter()
    {
        var stats = new FrameStats();
        stats.Add(0.016);
        var batch = new SpriteBatch();

        DebugOverlay.Draw(batch, stats, "GPU", 800, 600);

        var visible = DebugOverlay.Lines(stats, "GPU", 800, 600).Sum(l => l.Count(c => c != ' '));
        Assert.Equal(1 + visible, batch.QuadCount);
        Assert.False(batch.Overflowed);
    }

    [Fact]
    public void PngWriter_WritesAValidPngThatDecodesBackToTheSamePixels()
    {
        var pixels = new byte[3 * 2 * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        using var stream = new MemoryStream();
        PngWriter.Write(stream, 3, 2, pixels);
        var bytes = stream.ToArray();

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
        Assert.Equal(new byte[] { 8, 6 }, bytes[24..26]);
        Assert.Equal(new byte[] { 0xAE, 0x42, 0x60, 0x82 }, bytes[^4..]); // the well-known CRC of an empty IEND chunk

        var idat = FindChunk(bytes, "IDAT");
        using var decoded = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress))
        {
            zlib.CopyTo(decoded);
        }

        var raw = decoded.ToArray();
        Assert.Equal(2 * (1 + (3 * 4)), raw.Length);
        Assert.Equal(0, raw[0]);
        Assert.Equal(pixels[..12], raw[1..13]);
        Assert.Equal(0, raw[13]);
        Assert.Equal(pixels[12..], raw[14..]);
    }

    [Fact]
    public void PngWriter_RejectsWrongSizedData()
    {
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentException>(() => PngWriter.Write(stream, 2, 2, new byte[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngWriter.Write(stream, 0, 2, []));
    }

    [Fact]
    public void Input_PressedAndReleasedLastExactlyOneFrame()
    {
        var input = new InputState();

        input.SetKey((int)Key.W, down: true);

        Assert.True(input.IsDown(Key.W));
        Assert.True(input.WasPressed(Key.W));

        input.BeginFrame();

        Assert.True(input.IsDown(Key.W));
        Assert.False(input.WasPressed(Key.W));

        input.SetKey((int)Key.W, down: false);

        Assert.False(input.IsDown(Key.W));
        Assert.True(input.WasReleased(Key.W));

        input.BeginFrame();

        Assert.False(input.WasReleased(Key.W));
    }

    [Fact]
    public void Input_KeyRepeatsAndUnknownScancodesAreIgnored()
    {
        var input = new InputState();
        input.SetKey((int)Key.Space, down: true);
        input.BeginFrame();

        input.SetKey((int)Key.Space, down: true, isRepeat: true);
        input.SetKey(-1, down: true);
        input.SetKey(100000, down: true);

        Assert.False(input.WasPressed(Key.Space));
        Assert.False(input.IsDown((Key)100000));
    }

    [Fact]
    public void Input_ReleaseAllLetsGoOfEverythingHeld()
    {
        var input = new InputState();
        input.SetKey((int)Key.A, down: true);
        input.SetKey((int)Key.LeftShift, down: true);
        input.SetMouseButton((int)MouseButton.Left, down: true);
        input.BeginFrame();

        input.ReleaseAll();

        Assert.False(input.IsDown(Key.A));
        Assert.True(input.WasReleased(Key.LeftShift));
        Assert.False(input.IsDown(MouseButton.Left));
    }

    [Fact]
    public void Input_MouseDeltaAccumulatesWithinAFrameAndResetsAfter()
    {
        var input = new InputState();

        input.MoveMouse(10, 20, 3, -2);
        input.MoveMouse(14, 19, 4, -1);

        Assert.Equal((14f, 19f), (input.MouseX, input.MouseY));
        Assert.Equal((7f, -3f), (input.MouseDeltaX, input.MouseDeltaY));

        input.BeginFrame();

        Assert.Equal((0f, 0f), (input.MouseDeltaX, input.MouseDeltaY));
        Assert.Equal(14f, input.MouseX);
    }

    [Fact]
    public void Input_MouseButtonsReportPressAndHold()
    {
        var input = new InputState();

        input.SetMouseButton((int)MouseButton.Right, down: true);

        Assert.True(input.WasPressed(MouseButton.Right));
        Assert.True(input.IsDown(MouseButton.Right));

        input.BeginFrame();

        Assert.False(input.WasPressed(MouseButton.Right));
        Assert.True(input.IsDown(MouseButton.Right));
        input.SetMouseButton(99, down: true);
    }

    private static int CountLit(byte[] atlas, (float U0, float V0, float U1, float V1) uv, int onlyColumn = -1, int onlyRow = -1)
    {
        var x0 = (int)Math.Round(uv.U0 * DebugFont.AtlasWidth);
        var y0 = (int)Math.Round(uv.V0 * DebugFont.AtlasHeight);
        var x1 = (int)Math.Round(uv.U1 * DebugFont.AtlasWidth);
        var y1 = (int)Math.Round(uv.V1 * DebugFont.AtlasHeight);
        var lit = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var inFilter = (onlyColumn < 0 || x - x0 == onlyColumn) && (onlyRow < 0 || y - y0 == onlyRow);
                if (inFilter && atlas[(y * DebugFont.AtlasWidth) + x] != 0)
                {
                    lit++;
                }
            }
        }

        return lit;
    }

    private static byte[] FindChunk(byte[] png, string type)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            var name = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (name == type)
            {
                return png[(offset + 8)..(offset + 8 + length)];
            }

            offset += 12 + length;
        }

        throw new InvalidOperationException($"no {type} chunk");
    }
}
