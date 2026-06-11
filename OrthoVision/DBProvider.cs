using Microsoft.Data.SqlClient;
using OrthoVisionDriver;
using OrthoVisionDriver.Interfaces;
using OrthoVisionDriver.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace OrthoVision
{
    // объект данных для формирования задания
    public class OrderData
    {
        public string rid { get; set; }
        public string pid { get; set; }
        public string patientSurname { get; set; }
        public string patientName { get; set; }
        public string patientSex { get; set; }
        public string patientBirthDay { get; set; }
        public List<string> profiles { get; set; }
    }

    public class DBProvider : IDBProvider
    {
        private readonly ILoggerService logger;

        private readonly string user = "mielogrammauser";
        private readonly string password = "Qw123456";

        private readonly string connectionString;
        private readonly string analyzerCode = "914";                   // код из аналайзер конфигурейшн, который связывает прибор в PSMV2 ;
        private readonly string analyzerConfigurationCode = "ORTHOVSN"; // код прибора из аналайзер конфигурейшн;

        public List<string> profiles;

        public DBProvider(ILoggerService logger, string connectionString)
        {
            this.logger = logger;
            this.connectionString = string.Concat(connectionString, $"User Id = {user}; Password = {password}");//connectionString;
        }

        /// <summary>
        /// Функция преобразования кода теста прибора в код теста PSMV2 в CGM
        /// </summary>
        public string TranslateToPSMCodes(string AnalyzerTestCodesPar)
        {
            string BackTestCode = "";
            try
            {
                using (SqlConnection Connection = new SqlConnection(connectionString))
                {
                    Connection.Open();
                    // Ищем только тесты, которые настроены для прибора exias и настроены для PSMV2
                    SqlCommand TestCodeCommand = new SqlCommand(
                       "SELECT k1.amt_analyskod  FROM konvana k " +
                       "LEFT JOIN konvana k1 ON k1.met_kod = k.met_kod AND k1.ins_maskin = 'PSMV2' " +
                       $"WHERE k.ins_maskin = '{analyzerConfigurationCode}' AND k.amt_analyskod = '{AnalyzerTestCodesPar}' ", Connection);
                    SqlDataReader Reader = TestCodeCommand.ExecuteReader();

                    if (Reader.HasRows) // если есть данные
                    {
                        while (Reader.Read())
                        {
                            if (!Reader.IsDBNull(0)) { BackTestCode = Reader.GetString(0); }
                        }
                    }
                    Reader.Close();
                    Connection.Close();
                }
            }
            catch (Exception error)
            {

            }

            return BackTestCode;
        }

        /// <summary>
        /// Функция запроса задания в ЛИС
        /// </summary>
        public string GetRequestFromDB(string RID)
        {
            OrderData order_data = new OrderData();
            profiles = new List<string>();

            bool RIDExists = false;

            try
            {
                using (SqlConnection Connection = new SqlConnection(connectionString))
                {
                    Connection.Open();
                    #region ищем RID и получаем данные по нему из БД
                    //ищем RID в базе
                    SqlCommand RequetDataCommand = new SqlCommand(
                       "SELECT TOP 1" +
                         "p.pop_pid AS PID, p.pop_enamn AS PatientSurname, p.pop_fnamn AS PatientName, p.pop_fdatum AS PatientBirthday, " +
                         "CASE WHEN p.pop_kon = 'K' THEN 'F' ELSE 'M' END AS PatientSex, " +
                         "r.rem_ank_dttm AS RegistrationDate " +
                       "FROM dbo.remiss (NOLOCK) r " +
                         "INNER JOIN dbo.pop (NOLOCK) p ON p.pop_pid = r.pop_pid " +
                       "WHERE r.rem_deaktiv = 'O' " +
                         $"AND r.rem_rid IN ('{RID}') " +
                         "AND r.rem_ank_dttm IS NOT NULL ", Connection);
                    SqlDataReader Reader = RequetDataCommand.ExecuteReader();

                    // если такой ШК есть
                    if (Reader.HasRows)
                    {
                        RIDExists = true;
                        // получаем данные по заявке
                        while (Reader.Read())
                        {
                            if (!Reader.IsDBNull(0)) { order_data.pid = Reader.GetString(0); }
                            if (!Reader.IsDBNull(1)) { order_data.patientSurname = Reader.GetString(1); }
                            if (!Reader.IsDBNull(2)) { order_data.patientName = Reader.GetString(2); }
                            if (!Reader.IsDBNull(3))
                            {
                                DateTime patientBirthDay = Reader.GetDateTime(3);
                                order_data.patientBirthDay = patientBirthDay.ToString("yyyyMMddHH");
                            }
                            if (!Reader.IsDBNull(4)) { order_data.patientSex = Reader.GetString(4); }
                        }
                    }
                    Reader.Close();

                    // если шк есть, получаем тесты
                    if (RIDExists)
                    {
                        // в качестве задания нужно получить тесты которые не отвалидированы
                        // либо тесты с которых снята валидация (Reject) - b.bes_svarstat = 'U, 
                        // либо новые тесты (b.bes_svarstat IS NULL AND b.bes_antalomg = 0), зарегистрированные и без результата

                        SqlCommand TestCodeCommand = new SqlCommand(
                            "SELECT b.ana_analyskod, prov.pro_provdat " +
                            "FROM dbo.remiss (NOLOCK) r " +
                              "INNER JOIN dbo.bestall (NOLOCK) b ON b.rem_id = r.rem_id " +
                              "INNER JOIN dbo.prov (NOLOCK) prov ON prov.pro_id = b.pro_id " +
                              "LEFT JOIN dbo.omgang (NOLOCK) o ON b.rem_id = o.rem_id AND b.pro_id = o.pro_id AND b.ana_analyskod = o.ana_analyskod " +
                            "WHERE r.rem_deaktiv = 'O' " +
                            $"AND r.rem_rid IN('{RID}') " +
                            "AND r.rem_ank_dttm IS NOT NULL " +
                            "AND o.omg_resultat IS NULL " + // и если нет никакого результата
                                                            //либо тесты с которых снята валидация (Reject), либо новые тесты (b.bes_svarstat IS NULL AND b.bes_antalomg = 0)
                                                            //"AND (b.bes_svarstat = 'U' OR (b.bes_svarstat IS NULL AND b.bes_antalomg = 0))", CGMconnection);
                            "AND (b.bes_svarstat = 'U' OR (b.bes_svarstat IS NULL AND b.bes_antalomg = 0))", Connection);

                        SqlDataReader TestsReader = TestCodeCommand.ExecuteReader();

                        logger.LogExchange($"Заявка {RID} существует в ЛИС.");

                        // Если задания есть
                        if (TestsReader.HasRows)
                        {
                            while (TestsReader.Read())
                            {
                                if (!TestsReader.IsDBNull(0))
                                {
                                    // создаем список и добавляем в него профили, в зависимости от тестов
                                    string LISTestCode = TestsReader.GetString(0);
                                    string profileCode = TranslateLISCodeToProfileCode(LISTestCode);

                                    if (!String.IsNullOrEmpty(profileCode))
                                    {
                                        if (!profiles.Contains(profileCode))
                                        {
                                            profiles.Add(profileCode);
                                        }
                                    }
                                }
                            }
                        }
                        TestsReader.Close();
                    }
                    #endregion

                    Connection.Close();
                }

                // список с профилями не пустой, тогда нужно отправить задание
                if(profiles.Count > 0)
                {
                    
                }
                
            }
            catch(Exception ex)
            {
                logger.LogExchange($"Ошибка при получении заказа в ЛИС. \r {ex.ToString} ");
            }
        }

        /// <summary>
        /// Создаёт ответное ASTM-сообщение с заказами для прибора (согласно спецификации)
        /// </summary>
        private string CreateOrderMessage(OrderData order)
        {
            var orderString = new StringBuilder();

            orderString.AppendLine($"H|\\^&|||LIS CGM||||||||LIS2-A|{DateTime.Now:yyyyMMddHHmmss}");
            orderString.AppendLine($"P|1|{order.pid}|||{order.patientSurname}^{order.patientName}^||{order.patientBirthDay}|{order.patientSex}");

            int Onum = 0;
            foreach(string profile in order.profiles)
            {
                orderString.AppendLine($"O|{++Onum}|{order.rid}||{profile}|N|{DateTime.Now:yyyyMMddHHmmss}|||||N||||CENTBLOOD");
            }

            orderString.AppendLine($"L");

            return orderString.ToString();
        }

        /// <summary>
        /// Функция преобразования кода теста ЛИСа в соответствующий код профиля анализатора
        /// </summary>
        public string TranslateLISCodeToProfileCode(string profileCode)
        {
            switch (profileCode)
            {
                case "Ф0210": return "1_ABO/Rh";
                case "Ф0215": return "1_ABO/Rh";
                case "Ф0218": return "2_Pheno/Kell";
                case "Ф0219": return "2_Pheno/Kell";
                case "Ф0240": return "3_AT/Kell";
                default: return null;
            }
        }
    }
}
