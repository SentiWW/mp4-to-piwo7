using System.Buffers;
using System.Text;

using ImageMagick;

using MediaToolkit;
using MediaToolkit.Model;
using MediaToolkit.Options;

var currentDirectory = Directory.GetCurrentDirectory();
var dataDirectory = Path.Combine(currentDirectory, "Data");
var inputVideoPath = Path.Combine(dataDirectory, "video.mp4");
var outputAudioPath = Path.Combine(dataDirectory, "audio.mp3");
var outputFramesPath = Path.Combine(dataDirectory, "Frames");

var piwoHeader = $"PIWO_7_FILE{Environment.NewLine}" +
                      $"12 10{Environment.NewLine}";

if (!Directory.Exists(dataDirectory))
    Directory.CreateDirectory(dataDirectory);

if (!Directory.Exists(outputFramesPath))
    Directory.CreateDirectory(outputFramesPath);

using var engine = new Engine();
var inputFile = new MediaFile
{
    Filename = inputVideoPath
};

engine.GetMetadata(inputFile);

// Extract audio from mp4
engine.CustomCommand($"-i {inputVideoPath} -vn -acodec libmp3lame -qscale:a 2 {outputAudioPath}");

Console.WriteLine($"Audio extracted to {outputAudioPath}");

const int width = 12;
const int height = 10;

var resizeSize = new MagickGeometry(width, height)
{
    IgnoreAspectRatio = true
};

var outputFile = new MediaFile();
var options = new ConversionOptions();

var outputBuilder = new StringBuilder();

var totalFrames = Math.Floor(inputFile.Metadata.VideoData.Fps * inputFile.Metadata.Duration.TotalSeconds);
foreach (var interpolateMethod in Enum.GetValues<PixelInterpolateMethod>())
{
    if (interpolateMethod is PixelInterpolateMethod.Undefined)
        continue;

    var interpolateMethodName = Enum.GetName(interpolateMethod)!.ToLower();
    var piwoOutputPath = Path.Combine(dataDirectory, $"output-{interpolateMethodName}.piwo7");

    if (File.Exists(piwoOutputPath))
        File.Delete(piwoOutputPath);
    
    outputBuilder.AppendLine(piwoHeader);
    
    for (var currentFrameNumber = 0; currentFrameNumber < totalFrames; currentFrameNumber++)
    {
        // Get raw frame
        var rawFrameFilePath = Path.Combine(outputFramesPath, $"raw-frame-{currentFrameNumber}.bmp");
        if (!File.Exists(rawFrameFilePath))
        {
            outputFile.Filename = rawFrameFilePath;
            options.Seek = TimeSpan.FromSeconds(currentFrameNumber / inputFile.Metadata.VideoData.Fps);
            engine.GetThumbnail(inputFile, outputFile, options);
        }

        // Resize frame and interpolate
        using var frame = new MagickImage(rawFrameFilePath);
        frame.InterpolativeResize(resizeSize, interpolateMethod);
        
        // Convert frame to piwo7
        var buffer = ArrayPool<string>.Shared.Rent(width * height);
        var pixels = frame.GetPixels();
        foreach (var pixel in pixels)
        {
            var color = pixel.ToColor();

            if (color is null)
                throw new NullReferenceException("Color was null.");
            
            var red = (byte)color.R;
            var green = (byte)color.G;
            var blue = (byte)color.B;
            var union = (red << 16) | (green << 8) | blue;
            buffer[pixel.Y * width + pixel.X] = union.ToString();
        }
    
        // Append piwo7 frame to file
        outputBuilder.AppendLine("50");
        for (var bufferColumn = 0; bufferColumn < height; bufferColumn++)
        {
            for (var bufferRow = 0; bufferRow < width; bufferRow++)
            {
                outputBuilder.Append($"{buffer[bufferColumn * width + bufferRow]} ");
            }
            outputBuilder.AppendLine();
        }
        outputBuilder.AppendLine();

        Console.WriteLine($"Frames processed ({interpolateMethodName} interpolation): {currentFrameNumber + 1}/{totalFrames}");
        
        ArrayPool<string>.Shared.Return(buffer);
    }
    
    await using var writer = new StreamWriter(piwoOutputPath);
    await writer.WriteAsync(outputBuilder.ToString());
    await writer.FlushAsync();
    writer.Close();
    outputBuilder.Clear();
}