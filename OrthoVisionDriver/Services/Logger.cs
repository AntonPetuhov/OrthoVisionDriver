using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using OrthoVisionDriver.Interfaces;

namespace OrthoVisionDriver.Services
{
    public enum LoggerType
    {
        Service,        // Общий лог службы
        TcpIp,          // Лог TCP/IP соединения
        Exchange,       // Лог обмена ASTM-сообщениями (запросы/ответы)
        Result          // Лог обработчика файлов результатов
    }
    public class Logger : ILoggerService
    {
        public LoggerType loggerType { get; set; }      // тип логгера
        public string logsDirectory { get; set; }       // директория логов

        private readonly object locker = new object();
        private readonly object ServiceLogLocker = new object();     // локер для логов сервиса
        private readonly object ExchangeLogLocker = new object();    // локер для логов обмена
        private readonly object FileResultLogLocker = new object();  // локер для логов обработки результатов
        private readonly object TCPServerLocker = new object();      // локер для логов TCP сервера

        /*
        public Logger(LoggerType loggerType, string analyzerPath)
        {
            this.loggerType = loggerType;

            logsDirectory = Path.Combine(analyzerPath, "Logs");
            if (!Directory.Exists(logsDirectory))
            {
                Directory.CreateDirectory(logsDirectory);
            }
        }
        */
        public Logger(string analyzerPath) 
        {
            logsDirectory = Path.Combine(analyzerPath, "Logs");
            if (!Directory.Exists(logsDirectory))
            {
                Directory.CreateDirectory(logsDirectory);
            }
        }

        public void serviceLog(string message) => Write(LoggerType.Service,  message);
        public void tcpLog(string message) => Write(LoggerType.TcpIp,  message);
        //public void serviceLog(string message) => Write(LoggerType.Service, ServiceLogLocker, message);
        //public void tcpLog(string message) => Write(LoggerType.TcpIp, TCPServerLocker, message);
        //public void exchangeLog(string message) => Write(LoggerType.Exchange, ExchangeLogLocker, message);
        //public void resultHandlerLog(string message) => Write(LoggerType.ResultHandler, FileResultLogLocker, message);

        public void LogService(string message) => Write(LoggerType.Service, message);
        public void LogTcp(string message) => Write(LoggerType.TcpIp, message);
        public void LogExchange(string message) => Write(LoggerType.Exchange, message);
        public void LogResult(string message) => Write(LoggerType.Result, message);

        private void Write(LoggerType loggerType, string message)
        {
            lock (locker)
            {
                try
                {
                    string currentLogPath = Path.Combine(logsDirectory, loggerType.ToString());
                    if (!Directory.Exists(currentLogPath))
                    {
                        Directory.CreateDirectory(currentLogPath);
                    }

                    string logfileName = currentLogPath + $"\\{loggerType}Log_" + DateTime.Now.ToShortDateString().Replace('/', '_') + ".txt";

                    if (!File.Exists(logfileName))
                    {
                        using (StreamWriter writer = File.CreateText(logfileName))
                        {
                            writer.WriteLine(DateTime.Now + ": " + message);
                        }
                    }
                    else
                    {
                        using (StreamWriter writer = File.AppendText(logfileName))
                        {
                            writer.WriteLine(DateTime.Now + ": " + message);
                        }
                    }

                    //Thread.Sleep(3000);
                }
                catch
                {

                }
            }
        }

        // в случае, когда создается один объект логгера
        private void Write_(LoggerType loggerType, object locker , string message) 
        {
            lock (locker) 
            {
                try
                {
                    string currentLogPath = Path.Combine(logsDirectory, loggerType.ToString());
                    if (!Directory.Exists(currentLogPath))
                    {
                        Directory.CreateDirectory(currentLogPath);
                    }

                    string logfileName = currentLogPath + $"\\{loggerType}Log_" + DateTime.Now.ToShortDateString().Replace('/', '_') + ".txt";

                    if (!File.Exists(logfileName))
                    {
                        using (StreamWriter writer = File.CreateText(logfileName))
                        {
                            writer.WriteLine(DateTime.Now + ": " + message);
                        }
                    }
                    else
                    {
                        using (StreamWriter writer = File.AppendText(logfileName))
                        {
                            writer.WriteLine(DateTime.Now + ": " + message);
                        }
                    }

                    Thread.Sleep(3000);
                }
                catch
                {

                }
            }
        }

        // Если создаем объект логгера для каждого типа логгирования
        public void Log(string message)
        {
            lock (locker)
            {
                try
                {
                    string currentLogPath = Path.Combine(logsDirectory, loggerType.ToString());
                    if (!Directory.Exists(currentLogPath))
                    {
                        Directory.CreateDirectory(currentLogPath);
                    }

                    string logfileName = currentLogPath + $"\\{loggerType}Log_" + DateTime.Now.ToShortDateString().Replace('/', '_') + ".txt";

                    if (!File.Exists(logfileName))
                    {
                        using (StreamWriter writer = File.CreateText(logfileName))
                        {
                            writer.WriteLine(DateTime.Now + ": " + message);
                        }
                    }
                    else
                    {
                        using (StreamWriter writer = File.AppendText(logfileName))
                        {
                            writer.WriteLine(DateTime.Now + ": " + message);
                        }
                    }

                    Thread.Sleep(3000);
                }
                catch { }
            }
        }
    }
}
