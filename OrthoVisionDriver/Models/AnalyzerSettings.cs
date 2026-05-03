using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrthoVisionDriver.Models
{
    public class AnalyzerSettings
    {
        public string analyzerId { get; set; }      // Уникальный ID прибора
        public string analyzerName { get; set; }    // Уникальный ID прибора
        public string connectionType { get; set; }  // "TCPIP", "Serial", "File"
        public string ipAddress { get; set; }   // IP-адрес, на котором слушаем (0.0.0.0)
        public int port { get; set; }           // Порт для TCP/IP
        public bool isdll { get; set; }         // подключен с помощью dll?
        public string dllPath { get; set; }     // Путь к dll с реализацией протокола

        // Статусы активности (управление потоками)
        public bool activeStatus { get; set; }           // статус работы прибора
        public bool workStatus { get; set; }             // запускать ли поток обмена сообщениями с прибором
        public bool resultHandlerStatus { get; set; }    // запускать ли поток обработки результатов

        // Папки для сохранения результатов и логов
        public string resultsFolder { get; set; }        // Корневая папка для результатов
        public string logsFolder { get; set; }           // Папка для логов
    }
}
