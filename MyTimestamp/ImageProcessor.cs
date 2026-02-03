using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.IO;

namespace MyTimestamp
{
    public static class ImageProcessor
    {
        // Convert WPF BitmapSource to System.Drawing.Bitmap
        public static Bitmap ToWinFormsBitmap(BitmapSource bitmapsource)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BitmapEncoder enc = new BmpBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bitmapsource));
                enc.Save(stream);

                using (var temp = new Bitmap(stream))
                {
                    return new Bitmap(temp);
                }
            }
        }

        // Convert System.Drawing.Bitmap to WPF BitmapImage
        public static BitmapImage ToWpfBitmap(Bitmap bitmap)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Bmp);
                stream.Position = 0;
                BitmapImage result = new BitmapImage();
                result.BeginInit();
                result.CacheOption = BitmapCacheOption.OnLoad;
                result.StreamSource = stream;
                result.EndInit();
                result.Freeze();
                return result;
            }
        }

        public static Bitmap Process(Bitmap original, bool invert, bool binarize, int threshold, bool dilate)
        {
            // Lock bits for fast processing
            Bitmap data = (Bitmap)original.Clone();
            BitmapData bmpData = data.LockBits(new Rectangle(0, 0, data.Width, data.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

            int bytes = Math.Abs(bmpData.Stride) * data.Height;
            byte[] rgbValues = new byte[bytes];

            Marshal.Copy(bmpData.Scan0, rgbValues, 0, bytes);

            // 1. Invert & Binarize (Pixel by Pixel)
            for (int i = 0; i < bytes; i += 4)
            {
                // BGRA
                byte b = rgbValues[i];
                byte g = rgbValues[i + 1];
                byte r = rgbValues[i + 2];
                // byte a = rgbValues[i + 3];

                if (invert)
                {
                    b = (byte)(255 - b);
                    g = (byte)(255 - g);
                    r = (byte)(255 - r);
                }

                if (binarize)
                {
                    // Grayscale
                    double gray = (r * 0.299 + g * 0.587 + b * 0.114);
                    byte val = (gray > threshold) ? (byte)255 : (byte)0;
                    b = g = r = val;
                }
                else
                {
                    // Write back if just inverted
                     if (invert)
                     {
                         rgbValues[i] = b;
                         rgbValues[i + 1] = g;
                         rgbValues[i + 2] = r;
                     }
                }
                
                if (binarize)
                {
                     rgbValues[i] = b;
                     rgbValues[i + 1] = g;
                     rgbValues[i + 2] = r;
                }
            }

            Marshal.Copy(rgbValues, 0, bmpData.Scan0, bytes);
            data.UnlockBits(bmpData);

            // 2. Dilate (Morphology)
            // Connection of dotted fonts.
            // Simple logic: If a pixel is white (text), make its neighbors white.
            // Assuming Text is WHITE and Background is BLACK after Binarization.
            // If user has black text on white bg, Dilate might erode the text.
            // Typically OCR wants Black Text on White BG.
            // Tesseract prefers Black text. Windows OCR works with both but prefers high contrast.
            // Let's assume we want to "thicken" the foreground.
            
            if (dilate)
            {
                // For dilation, we need a copy of the source to read from while writing to dest.
                // Simple 3x3 max filter.
                Bitmap dilated = new Bitmap(data.Width, data.Height);
                
                // Using GetPixel/SetPixel is slow, but for small ROIs (timestamps) it is instantaneous.
                // For 4K full frame auto-detect it might be slow. 
                // Let's stick to LockBits if possible or just accept slow performance for the "Preprocess" check?
                // Given we are doing this on the High-Res crop, the crop is small (e.g. 500x100).
                // If we do it on Full Frame 4K for Auto-Detect, it will be SLOW (seconds).
                // Recommendation: Only apply Dilation to the Crop for OCR.
                // For Auto-Detect, maybe skip Dilation or downscale first.
                
                // Fast Dilation Implementation
               dilated = DilateFast(data);
               data.Dispose();
               return dilated;
            }

            return data;
        }

        private static Bitmap DilateFast(Bitmap src)
        {
            Bitmap dst = new Bitmap(src.Width, src.Height);
            BitmapData srcData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dstData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            int stride = srcData.Stride;
            int bytes = Math.Abs(stride) * src.Height;
            byte[] srcBytes = new byte[bytes];
            byte[] dstBytes = new byte[bytes];

            Marshal.Copy(srcData.Scan0, srcBytes, 0, bytes);

            // Simple 3x3 kernel Dilation (Max)
            int width = src.Width;
            int height = src.Height;
            
            // Assume 32bpp (4 bytes per pixel)
            Parallel.For(1, height - 1, y =>
            {
                for (int x = 1; x < width - 1; x++)
                {
                    // Find max brightness in 3x3
                    byte maxVal = 0;
                    
                    // Center
                    int centerIdx = (y * stride) + (x * 4);
                    
                    // We only need to check one channel if binarized (R=G=B)
                    // Let's check Red (offset 2)
                    
                    // Kernel loop
                    for (int ky = -1; ky <= 1; ky++)
                    {
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            int idx = ((y + ky) * stride) + ((x + kx) * 4);
                             byte val = srcBytes[idx + 2]; // Red
                             if (val > maxVal) maxVal = val;
                        }
                    }

                    // Set output
                    dstBytes[centerIdx] = maxVal;     // B
                    dstBytes[centerIdx + 1] = maxVal; // G
                    dstBytes[centerIdx + 2] = maxVal; // R
                    dstBytes[centerIdx + 3] = 255;    // A
                }
            });

            Marshal.Copy(dstBytes, 0, dstData.Scan0, bytes);
            
            src.UnlockBits(srcData);
            dst.UnlockBits(dstData);
            
            return dst;
        }
    }
}
