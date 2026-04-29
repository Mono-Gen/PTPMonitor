using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

class PngToIco {
    static void Main(string[] args) {
        if (args.Length < 2) {
            Console.WriteLine("Usage: PngToIco <input.png> <output.ico>");
            return;
        }

        string inputPath = args[0];
        string outputPath = args[1];

        using (Bitmap bitmap = (Bitmap)Image.FromFile(inputPath)) {
            using (FileStream stream = new FileStream(outputPath, FileMode.Create)) {
                // ICONDIR header
                stream.WriteByte(0); stream.WriteByte(0); // Reserved
                stream.WriteByte(1); stream.WriteByte(0); // Type (1 = icon)
                stream.WriteByte(1); stream.WriteByte(0); // Count (1 image)

                // ICONDIRENTRY
                int width = bitmap.Width >= 256 ? 0 : bitmap.Width;
                int height = bitmap.Height >= 256 ? 0 : bitmap.Height;
                stream.WriteByte((byte)width);
                stream.WriteByte((byte)height);
                stream.WriteByte(0); // Colors
                stream.WriteByte(0); // Reserved
                stream.WriteByte(1); stream.WriteByte(0); // Planes
                stream.WriteByte(32); stream.WriteByte(0); // BitCount

                // Image data size and offset
                using (MemoryStream ms = new MemoryStream()) {
                    bitmap.Save(ms, ImageFormat.Png);
                    byte[] pngData = ms.ToArray();

                    uint size = (uint)pngData.Length;
                    stream.Write(BitConverter.GetBytes(size), 0, 4);
                    
                    uint offset = 6 + 16; // Header (6) + Entry (16)
                    stream.Write(BitConverter.GetBytes(offset), 0, 4);

                    // Write PNG data
                    stream.Write(pngData, 0, pngData.Length);
                }
            }
        }
        Console.WriteLine("Successfully converted " + inputPath + " to " + outputPath);
    }
}
