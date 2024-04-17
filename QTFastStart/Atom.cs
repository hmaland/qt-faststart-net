namespace QTFastStart
{
    public class Atom
    {
        public string Name { get; set; }
        public long Position { get; set; }
        public long Size { get; set; }
        public Atom(string name, long position, long size)
        {
            Name = name;
            Position = position;
            Size = size;
        }
    }
}
