using Serilog;
using System;
using System.Collections.Generic;
using System.Timers;
using System.IO;
using System.ServiceProcess;
using System.Configuration;
using System.Net.Sockets;
using SnmpSharpNet;
using System.Threading;

namespace UPSStatusService
{
    public partial class UPSStatusService : ServiceBase
    {
        private System.Timers.Timer timer;
        private NetworkStream stream;
        private TcpClient client;
        private readonly int secondsBetweenRequest = Convert.ToInt16(ConfigurationManager.AppSettings["secondsBetweenRequest"]);
        private readonly string ipUps = ConfigurationManager.AppSettings["ipUps"];
        private readonly string ipSms = ConfigurationManager.AppSettings["ipSms"];
        private readonly int portUps = Convert.ToInt32(ConfigurationManager.AppSettings["portUps"]);
        private readonly int portSms = Convert.ToInt32(ConfigurationManager.AppSettings["portSms"]);
        private readonly string[] phoneNumbers = ConfigurationManager.AppSettings["phoneNumbers"].Split(',');
        private string status = string.Empty;
        public UPSStatusService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(logDir, "log.txt"), rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information("Service started.");

            timer = new System.Timers.Timer
            {
                Interval = 1000 * secondsBetweenRequest
            };
            timer.Elapsed += Timer_Elapsed;
            timer.Start();
        }

        protected override void OnStop()
        {
            Log.Information("Service stopped.");

            timer.Stop();
            timer.Dispose();

            Log.CloseAndFlush();
        }

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                SimpleSnmp snmpVerb = new SimpleSnmp(ipUps, portUps, "public");
                if (!snmpVerb.Valid)
                {
                    Log.Warning("Can't connect to the UPS");
                    return;
                }

                Oid oidOutputSorce = new Oid("1.3.6.1.4.1.476.1.42.3.9.20.1.20.1.2.1.4872");
                Oid oidBatteryTimeRemaining = new Oid("1.3.6.1.4.1.476.1.42.3.9.20.1.20.1.2.1.4150");

                Dictionary<Oid, AsnType> response = snmpVerb.Get(SnmpVersion.Ver2, new string[] { oidOutputSorce.ToString(), oidBatteryTimeRemaining.ToString() });

                if (response != null)
                {
                    if (response[oidOutputSorce].ToString() != status && status != string.Empty)
                    {
                        string message = $"UPS zmienil swoje zrodlo zasilania z {status} na {response[oidOutputSorce]} \\n\\nPozostaly czas pracy baterii: {response[oidBatteryTimeRemaining]} min \\n\\nWiadomosc wygenerowana przez GaskaUPSStatusService";

                        using (client = new TcpClient(ipSms, portSms))
                        {
                            ClientReceive();
                            Send("aLOGI G001 7705");
                            Thread.Sleep(500);

                            foreach (var item in phoneNumbers)
                            {
                                Send("aSMSS G001 " + item.Trim() + " N 167 " + message);
                                Thread.Sleep(5000);
                            }

                            Send("aLOGO G001");
                            client.Close();
                        }
                    }

                    if (response[oidOutputSorce].ToString() == "Battery" && Convert.ToUInt16(response[oidBatteryTimeRemaining].ToString()) <= 7)
                    {
                        string message = $"Krytycznie niski stan baterii UPS!\\n\\nPozostaly czas pracy na baterii: {response[oidBatteryTimeRemaining]} min\\n\\nWiadomosc wygenerowana przez GaskaUPSStatusService";
                        using (client = new TcpClient(ipSms, portSms))
                        {
                            ClientReceive();
                            Send("aLOGI G001 7705");
                            Thread.Sleep(500);

                            foreach (var item in phoneNumbers)
                            {
                                Send("aSMSS G001 " + item.Trim() + " N 167 " + message);
                                Thread.Sleep(3000);
                            }

                            Send("aLOGO G001");
                            client.Close();
                        }
                    }

                    status = response[oidOutputSorce].ToString();
                }
                else
                {
                    Log.Warning("Can't retrieve information from the UPS");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }
        public void Send(string wiadomosc)
        {
            try
            {
                NetworkStream message = client.GetStream();
                StreamWriter writer = new StreamWriter(message);
                writer.WriteLine(wiadomosc);
                writer.Flush();

                Log.Information("SMS Client message: " + wiadomosc);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }

        public void ClientReceive()
        {
            Byte[] bytes = new Byte[client.ReceiveBufferSize];
            stream = client.GetStream();
            int i;

            new Thread(() =>
            {
                try
                {
                    while ((i = stream.Read(bytes, 0, bytes.Length)) != 0)
                    {
                        string data_Polskie = System.Text.Encoding.UTF8.GetString(bytes, 0, i);
                        Log.Information("SMS Serwer response: " + data_Polskie);
                    }

                    client.Close();
                }
                catch (System.IO.IOException)
                {
                    client.Close();
                }
                catch  (Exception ex)
                {
                    Log.Error(ex.ToString());
                    client.Close();
                }

            }).Start();
        }
    }
}
