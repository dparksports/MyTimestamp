using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using System.Drawing;

namespace MyTimestamp
{
    public class OcrResultModel
    {
        public string Text { get; set; } = string.Empty;
        public double Confidence { get; set; }
    }

    public interface IOcrService
    {
        string Name { get; }
        bool IsAvailable { get; }
        Task InitAsync();
        Task<OcrResultModel> RecognizeAsync(Bitmap image);
    }
}
