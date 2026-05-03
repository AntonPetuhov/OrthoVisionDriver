using Microsoft.Extensions.Logging;
using OrthoVisionDriver.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrthoVisionDriver.Services
{
    // для создания логгера "на лету" 
    public interface IAnalyzerLoggerFactory
    {
        ILoggerService CreateLogger(string folderPath);
    }

    public class AnalyzerLoggerFactory : IAnalyzerLoggerFactory
    {
        public ILoggerService CreateLogger(string folderPath) => new Logger(folderPath);
    }
}
