using System.Drawing;
using System.Drawing.Imaging;

namespace WinKVM.Agent;

/// Encodes framebuffer pixels to JPEG for AI provider submission.
public static class ScreenshotEncoder
{
    /// Encode BGRA pixels as JPEG. Optionally downscales to maxWidth.
    public static byte[] EncodeJpeg(byte[] bgraPixels, int width, int height,
                                     float quality = 0.85f, int maxWidth = 0)
    {
        int dstW = (maxWidth > 0 && width > maxWidth) ? maxWidth : width;
        int dstH = (maxWidth > 0 && width > maxWidth) ? (int)(height * ((double)maxWidth / width)) : height;

        using var srcBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bmpData = srcBmp.LockBits(new Rectangle(0, 0, width, height),
                                       ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(bgraPixels, 0, bmpData.Scan0, bgraPixels.Length);
        srcBmp.UnlockBits(bmpData);

        using var scaled = dstW == width ? srcBmp : new Bitmap(srcBmp, new Size(dstW, dstH));

        var qualityParam = new EncoderParameter(Encoder.Quality, (long)(quality * 100));
        var jpegCodec    = GetJpegCodec();
        var encParams    = new EncoderParameters(1) { Param = [qualityParam] };

        using var ms = new MemoryStream();
        scaled.Save(ms, jpegCodec, encParams);
        return ms.ToArray();
    }

    private static ImageCodecInfo GetJpegCodec()
        => ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
}
