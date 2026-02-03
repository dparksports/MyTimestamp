using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local; 
using Sdcb.PaddleInference;
using OpenCvSharp;

namespace MyTimestamp
{
    public class PaddleOcrService : IOcrService
    {
        public string Name => "PaddleOCR (Local)";
        public bool IsAvailable => true; 

        private PaddleOcrAll? _engine;

        public async Task InitAsync()
        {
            if (_engine != null) return;

            await Task.Run(() =>
            {
                // Full model (Detection + Classification + Recognition)
                // Disable rotation for stability on timestamps (often small/high contrast)
                _engine = new PaddleOcrAll(LocalFullModels.EnglishV3, PaddleDevice.Mkldnn())
                {
                    AllowRotateDetection = false, 
                    Enable180Classification = false,
                };
            });
        }

        public async Task<OcrResultModel> RecognizeAsync(System.Drawing.Bitmap image)
        {
            if (_engine == null) await InitAsync();
            if (_engine == null) throw new Exception("PaddleOCR Engine not initialized.");

            return await Task.Run(() =>
            {
                // Ensure 24bpp RGB to avoid Alpha channel issues with Paddle
                // Using a white background to prevent transparent pixels becoming black
                using (var bip24 = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
                {
                    using (var gr = Graphics.FromImage(bip24))
                    {
                        gr.Clear(Color.White);
                        gr.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height));
                    }
                    
                    using (var ms = new MemoryStream())
                    {
                        bip24.Save(ms, ImageFormat.Bmp);
                        ms.Position = 0;
                        
                        // Decode from stream to Mat
                        var bytes = ms.ToArray();
                        using (var srcMat = Cv2.ImDecode(bytes, ImreadModes.Color))
                        {
                            if (srcMat.Empty())
                            {
                                throw new Exception("One or more images could not be decoded by OpenCV.");
                            }

                            // Add padding to improve detection on tight crops or binarized inputs
                            using (var paddedMat = new Mat())
                            {
                                int pad = 32; // 32px white padding
                                Cv2.CopyMakeBorder(srcMat, paddedMat, pad, pad, pad, pad, BorderTypes.Constant, Scalar.White);
                                
                                try 
                                {
                                    var result = _engine.Run(paddedMat);
                                    
                                    // Result text
                                    return new OcrResultModel
                                    {
                                        Text = result.Text,
                                        Confidence = 0 
                                    };
                                }
                                catch (Exception ex)
                                {
                                    // If prediction fails (e.g. Detector error), return info or empty
                                    // Don't crash the app
                                    return new OcrResultModel { Text = $"(OCR Error: {ex.Message})" };
                                }
                            }
                        }
                    }
                }
            });
        }
    }
}
