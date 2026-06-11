using OrthoVisionDriver.Interfaces;
using OrthoVisionDriver.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OrthoVision
{
    public class ResultsHandler : IResultHandler
    {
        private readonly ILoggerService logger;
        private readonly string resultFolder;
        private readonly string outputFolder;
        private readonly string archiveFolder;
        private readonly string errorFolder;
        private readonly IDBProvider dbProvider;

        string analyzerCode; // код анализатора из CGM, нужен для формирования текста результирующего файла

        public ResultsHandler(ILoggerService logger, AnalyzerSettings settings, IDBProvider dbProvider)
        {
            this.logger = logger;
            resultFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.analyzerName, "Results");
            outputFolder = settings.outputFolder;
            archiveFolder = Path.Combine(resultFolder, "Archive");
            errorFolder = Path.Combine(resultFolder, "Error");
            this.dbProvider = dbProvider;

            analyzerCode = settings.analyzerCode;

            CreateDirectories();
        }

        /// <summary>
        /// Запуск мониторинга папки с результатами
        /// </summary>
        public void StartMonitoring(CancellationToken cancellationToken)
        {
            logger.LogResult($"Запуск потока мониторинга файлов с результатами в папке");

            Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    CreateDirectories();
                    // получаем список всех файлов в текущей папке
                    string[] Files = Directory.GetFiles(resultFolder, "*.res");
                    // пробегаем по файлам
                    foreach (string file in Files)
                    {
                        await ProcessResultFileAsync(file, cancellationToken);
                        await Task.Delay(100, cancellationToken);
                    }
                    await Task.Delay(1000, cancellationToken);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Чтение файла, парсинг, создание выходного файла .res + файла .ok
        /// </summary>
        public async Task ProcessResultFileAsync(string file, CancellationToken cancellationToken)
        {
            logger.LogResult(file);

            // обрезаем только имя текущего файла
            string FileName = file.Substring(resultFolder.Length + 1);
            // название файла .ок, который должен создаваться вместе с результирующим для обработки службой FileGetterService
            string OkFileName = "";

            // строки для формирования файла (psm файла) с результатами для службы,
            // которая разбирает файлы и записывает результаты в CGM
            string MessageHead = "";
            string MessageTest = "";
            string AllMessage = "";

            // шаблоны регулярных выражений для поиска данных
            string RIDPattern = @"\d+O\|(?:\d+)\|([^|]+)\|";
            string TestAndResultPattern = @"\d*R\|(?:\d+)\|([^|]+)\|([^|]+)\|";

            string RID = "";
            string Test = "";
            string Result = "";

            string[] lines = System.IO.File.ReadAllLines(file);
            // проходим по строкам в файле
            foreach (string line in lines)
            {
                // ищем номер заявки
                var RIDMatch = Regex.Match(line, RIDPattern);
                if (RIDMatch.Success)
                {
                    RID = RIDMatch.Groups[1].Value;
                    logger.LogResult($"Заявка № {RID}");
                    MessageHead = $"O|1|{RID}||ALL|R|{DateTime.Now.ToString("yyyyMMddHHmmss")}|||||X||||ALL||||||||||F";
                }

                // ищем код теста и результат
                var TestAndResultMatch = Regex.Match(line, TestAndResultPattern);
                if (TestAndResultMatch.Success)
                {
                    Test = TestAndResultMatch.Groups[1].Value;

                    string PSMTestCode = dbProvider.TranslateToPSMCodes(Test);
                    Result = TestAndResultMatch.Groups[2].Value;

                    //if (PSMTestCode == "")
                    if (string.IsNullOrEmpty(PSMTestCode))
                    {
                        logger.LogResult($"Код анализатора {Test} не интерпретирован в PSMV2 код.");
                        logger.LogResult($"{Test} - результат: {Result}");
                    }
                    else
                    {
                        logger.LogResult($"PSMV2 код: {PSMTestCode}");
                        logger.LogResult($"{Test} - результат: {Result}");
                    }

                    // если код тест был интерпретирован и найден результат
                    if ((PSMTestCode != "") && (Result != ""))
                    {
                        // формируем строку с ответом для результирующего файла
                        MessageTest = MessageTest + $"R|1|^^^{PSMTestCode}^^^^{analyzerCode}|{ResultInterpretation(Result)}|||N||F||ORTHOVISION^||20260101000001|{analyzerCode}" + "\r";
                    }
                }
            }

            // получаем название файла .ок на основании файла с результатом
            if (FileName.IndexOf(".") != -1)
            {
                OkFileName = FileName.Split('.')[0] + ".ok";
            }

            // если строки с результатами и с ШК не пустые, значит формируем результирующий файл
            if (!string.IsNullOrEmpty(MessageHead) && !string.IsNullOrEmpty(MessageTest))
            {
                try
                {
                    // собираем полное сообщение с результатом
                    AllMessage = MessageHead + "\r" + MessageTest;
                    logger.LogResult(AllMessage);

                    string fullPath = Path.Combine(outputFolder, FileName);
                    using (StreamWriter sw = new StreamWriter(fullPath, false, Encoding.GetEncoding(1251)))
                    {
                        foreach (string msg in AllMessage.Split('\r'))
                        {
                            sw.WriteLine(msg);
                        }
                    }

                    using (StreamWriter sw = new StreamWriter(Path.Combine(outputFolder, OkFileName), false, Encoding.GetEncoding(1251)))
                    {
                        sw.WriteLine("ok");
                    }

                    // помещение файла в архивную папку
                    File.Move(file, archiveFolder, overwrite: true);

                    logger.LogResult("Файл обработан и перемещен в папку Archive\r\n");

                }
                catch (Exception ex) 
                {
                    logger.LogResult(ex.ToString());
                    // помещение файла в папку с ошибками
                    File.Move(file, errorFolder, overwrite: true);

                    logger.LogResult("Ошибка обработки файла. Файл перемещен в папку Error\r\n");
                    logger.LogResult("");
                }
            }
            else
            {
                // помещение файла в папку с ошибками
                File.Move(file, errorFolder, overwrite: true);
                logger.LogResult("Ошибка обработки файла. Файл перемещен в папку Error\r\n");
            }
        }

        // интерпретация результатов
        private string ResultInterpretation(string AnalyzerResult)
        {
            switch (AnalyzerResult)
            {
                case "CCEE": return "C(+)c(-)E(+)e(-)";
                case "CCEe": return "C(+)c(-)E(+)e(+)";
                case "CCeE": return "C(+)c(-)E(-)e(-)";
                case "CCee": return "C(+)c(-)E(-)e(+)";

                case "CcEE": return "C(+)c(+)E(+)e(-)";
                case "CcEe": return "C(+)c(+)E(+)e(+)";
                case "CceE": return "C(+)c(+)E(-)e(-)";
                case "Ccee": return "C(+)c(+)E(-)e(+)";

                case "cCEE": return "C(-)c(-)E(+)e(-)";
                case "cCEe": return "C(-)c(-)E(+)e(+)";
                case "cCeE": return "C(-)c(-)E(-)e(-)";
                case "cCee": return "C(-)c(-)E(-)e(+)";

                case "ccEE": return "C(-)c(+)E(+)e(-)";
                case "ccEe": return "C(-)c(+)E(+)e(+)";
                case "cceE": return "C(-)c(+)E(-)e(-)";
                case "ccee": return "C(-)c(+)E(-)e(+)";
                case "A": return "Группа А (II)";
                case "AB": return "Группа АВ (IV)";
                case "B": return "Группа В (III)";
                case "O": return "Группа 0 (I)";
                case "A2": return "Группа А2(II)";
                case "A2B": return "Группа A2B(IV)";
                case "Поз.": return "положительный";
                case "Нег.": return "Отрицательный";
                case "POS": return "Положительный";
                case "NEG": return "Отрицательный";
                default: return AnalyzerResult;
            }
        }

        private void CreateDirectories()
        {
            foreach (var dir in new[] { resultFolder, outputFolder, archiveFolder, errorFolder })
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
    }
}
