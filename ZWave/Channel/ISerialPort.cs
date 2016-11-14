using System.IO;
using Windows.Storage.Streams;

namespace ZWave.Channel
{
    public interface ISerialPort
    {
        DataReader InputStream { get; }
        Stream OutputStream { get; }

        void Close();
        void Open();
    }
}