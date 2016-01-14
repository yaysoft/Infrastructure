﻿using Core.Interfaces.Logging;
using Core.Logging;
using Core.Logging.LogDestinationConfigs;
using Core.Logging.LogDestinations;
using Core.Logging.LogMessageFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LoggerTest.App
{
    public class Program
    {
        private static ILogger _logger;

        private static LogAbuser _abuser;

        private static ManualResetEvent _stop = new ManualResetEvent(false);
        private static ManualResetEvent _stopped = new ManualResetEvent(false);

        static void Main(string[] args)
        {
            _logger = new Logger();

            _logger.AddLogDestination(new ConsoleLogDestination());

            var config = new FileLogDestinationConfig();

            config.LogDirectory = ".\\logs";
            config.LogFileExtension = "csv";
            config.LogFilePrefix = "TestLog";
            config.LogMessageFormatter = new CSVLogMessageFormatter();
            config.MaxLogFileCount = 4;
            config.MaxLogFileSize = 5;

            _logger.AddLogDestination(new FileLogDestination(config));

            _logger.Start();

            Console.CancelKeyPress += Console_CancelKeyPress;

            _abuser = new LogAbuser(_logger);

            _abuser.SleepTimeInMilliseconds = 5000;

            _abuser.Start();

            while(!_stop.WaitOne(50))
            {
                if(Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;

                    switch(key)
                    {
                        case ConsoleKey.UpArrow:
                            _abuser.SleepTimeInMilliseconds = _abuser.SleepTimeInMilliseconds + 500;
                            break;
                        case ConsoleKey.DownArrow:
                            _abuser.SleepTimeInMilliseconds = _abuser.SleepTimeInMilliseconds - 500;

                            if(_abuser.SleepTimeInMilliseconds < 0)
                            {
                                _abuser.SleepTimeInMilliseconds = 1;
                            }

                            break;
                    }
                }
            }

            _abuser.Stop();

            _logger.Stop();

            _stopped.Set();
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            _stop.Set();
            _stopped.WaitOne();
        }
    }
}