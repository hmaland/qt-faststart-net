using QTFastStart;

namespace QTFastStartTestApp
{
    internal class Program
    {
        static int Main(string[] args)
        {
            if (args == null || args.Length < 2)
            {
                Console.WriteLine("Invalid argument(s).");
                Console.WriteLine(@"Usage:
QTFastStartTestApp <input-file> <output-file>
Examples:
QTFastStartTestApp MyMovie.mp4 MyMovie_Processed.mp4
");
                return 1;
            }
            var inputFile = args[0];
            var outputFile = args[1];
            var processor = new Processor();
            processor.Process(inputFile, outputFile); 
            return 0;
        }
    }
}
