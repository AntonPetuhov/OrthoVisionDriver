using OrthoVisionDriver.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrthoVisionDriver.Interfaces
{
    public interface ILoggerService
    {
        LoggerType loggerType { get; set; } // тип логгера
        // object locker { get; set; }         // локер для логов
        string logsDirectory { get; set; }  // папка для записи логов

        void LogService(string message);
        void LogTcp(string message);
        void LogExchange(string message);
        void LogResult(string message);

        void Log(string message);
    }
}
