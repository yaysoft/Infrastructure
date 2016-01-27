﻿using Core.Interfaces.Base;
using Core.Interfaces.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Linq;

namespace Core.Logging
{
    public sealed class Logger : Singleton<ILogger>, ILogger
    {
        #region Fields

        private Thread _logWorker;
        private List<ILogDestination> _destinations = new List<ILogDestination>();
        private Queue<LogMessage> _messageQueue = new Queue<LogMessage>();
        private Queue<LogMessage> _tempQueue = new Queue<LogMessage>();
        private ManualResetEvent _messagesReady = new ManualResetEvent(false);
        private ManualResetEvent _queueEmpty = new ManualResetEvent(false);

        private TimeSpan _queueTimeOut = TimeSpan.FromMilliseconds(100);
        private const int _defaultMaxQueueSize = 5000;
        private readonly string _processName;
        private readonly string _machineName;
        private readonly int _processId;
        private const int _blockSize = 64;

        private static object _syncObject = new object();

        #endregion

        #region Properties

        public IReadOnlyList<ILogDestination> LogDestinations
        {
            get
            {
                lock (_destinations)
                {
                    return _destinations.AsReadOnly();
                }
            }
        }

        public int MaxQueueSize { get; set; }
        public bool IsRunning { get; private set; }
        public bool IsPaused { get; private set; }

        #endregion

        #region Constructor

        private Logger()
        {
            MaxQueueSize = _defaultMaxQueueSize;

            _machineName = Environment.MachineName;

            var process = Process.GetCurrentProcess();

            _processName = process.ProcessName;
            _processId = process.Id;

            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        }

        #endregion

        #region Public Methods

        public static ILogger CreateInstance()
        {
            return Instance = new Logger();
        }

        public void AddLogDestination(ILogDestination logDestination)
        {
            if(IsRunning)
            {
                logDestination.Start();
            }

            lock(_destinations)
            {
                Log(string.Format("LogDestination of type \"{0}\" added.", logDestination.GetType().Name));

                _destinations.Add(logDestination);
            }
        }

        //pass through
        public void Log(LogMessage logMessage)
        {
            if (logMessage != null)
            {
                while (true)
                {
                    lock (_messageQueue)
                    {
                        Queue<LogMessage> queue = IsPaused ? _tempQueue : _messageQueue;
                        if (queue.Count < MaxQueueSize)
                        {
                            queue.Enqueue(logMessage);
                            _messagesReady.Set();
                            return;
                        }
                    }
                    // wait for queue to have room and keep trying
                    Thread.Sleep(100);
                }
            }
        }

        public void Log(string message, [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = -1)
        {
            Log(message, LogMessageCategory.General, LogMessageSeverity.Information, callerName, callerFilePath, callerLineNumber);
        }

        public void Log(string message, LogMessageSeverity severity, [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = -1)
        {
            Log(message, LogMessageCategory.General, severity, callerName, callerFilePath, callerLineNumber);
        }

        public void Log(string message, LogMessageCategory category, [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = -1)
        {
            Log(message, category, LogMessageSeverity.Information, callerName, callerFilePath, callerLineNumber);
        }

        public void Log(string message, LogMessageCategory category, LogMessageSeverity severity, [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = -1)
        {
           Log(CreateMessage(message, category, severity, callerName, callerFilePath, callerLineNumber));
        }

        private LogMessage CreateMessage(string message, LogMessageCategory category, LogMessageSeverity severity, [CallerMemberName] string callerName = "",
            [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = -1)
        {
            var logMessage = new LogMessage();

            logMessage.Category = category;
            logMessage.Severity = severity;
            logMessage.Message = message;
            logMessage.CallerName = callerName;
            logMessage.FilePath = callerFilePath;
            logMessage.LineNumber = callerLineNumber;
            logMessage.MachineName = _machineName;
            logMessage.ProcessId = _processId;
            logMessage.ProcessName = _processName;

            return logMessage;
        }

        public void RemoveLogDestination(ILogDestination logDestination)
        {
            throw new NotImplementedException();
        }

        public void Start()
        {
            lock (_syncObject)
            {
                if (!IsRunning)
                {
                    Log("Logger starting.");

                    lock (_destinations)
                    {
                        foreach (var destination in _destinations)
                        {
                            destination.Start();
                        }
                    }

                    _logWorker = new Thread(new ThreadStart(LogWorker));

                    _logWorker.Start();
                }
            }
        }

        public void Stop()
        {
            lock(_syncObject)
            {
                if (IsRunning)
                {
                    Log("Logger stopping.");

                    Flush();
                    IsRunning = false;

                    _logWorker.Join();

                    lock (_destinations)
                    {
                        foreach (var destination in _destinations)
                        {
                            destination.Stop();
                        }
                    }
                }
            }
        }

        public void Flush()
        {
            Pause();

            while(true)
            {
                _queueEmpty.WaitOne(_queueTimeOut);
                lock (_messageQueue)
                {
                    if (_messageQueue.Count == 0) { break; }
                }
            }
            List<ILogDestination> dests = new List<ILogDestination>();
            lock (_destinations)
            {
                dests.AddRange(_destinations);
            }
            foreach (ILogDestination dest in dests)
            {
                dest.Flush();
            }

            Resume();
        }

        #endregion

        #region Private Methods

        private void LogWorker()
        {
            IsRunning = true;

            while (IsRunning)
            {
                _messagesReady.WaitOne(_queueTimeOut);
                _messagesReady.Reset();

                int beforeQueue = 0;
                int messageSize = 0;


                List<LogMessage> messages = null;

                lock (_messageQueue)
                {
                    beforeQueue = _messageQueue.Count;
                    messages = _messageQueue.ToArray().Take(_blockSize).ToList();
                    messageSize = messages.Count;
                }

                if (messages.Count > 0)
                {
                    IEnumerable<ILogDestination> dests = null;
                    lock (_destinations)
                    {
                        dests = LogDestinations;
                    }
                    foreach (var destination in dests)
                    {
                        destination.ProcessMessages(messages);
                    }
                }
                lock (_messageQueue)
                {
                    if (messageSize > _messageQueue.Count) { System.Diagnostics.Debug.Assert(false); }
                    for (int x=0;x<messages.Count;x++)
                    {
                        _messageQueue.Dequeue();
                    }
                    if (_messageQueue.Count == 0) { _queueEmpty.Set(); }
                }
            }
        }

        private void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            if (IsRunning)
            {
                Log("Process exiting, shutting down logging system...", LogMessageSeverity.Warning);
                Stop();
            }
        }

        public void HandleLoggingException(string message, [CallerMemberName] string callerName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = -1)
        {
            LogMessage lm = CreateMessage(message, LogMessageCategory.General, LogMessageSeverity.Error, callerName, callerFilePath, callerLineNumber);
            List<ILogDestination> dests = new List<ILogDestination>();
            lock (_destinations)
            {
                dests.AddRange(_destinations);
            }
            foreach (var destination in dests)
            {
                destination.HandleLoggingException(lm);
            }
        }

        public void Pause()
        {
            IsPaused = true;
        }

        public void Resume()
        {
            IsPaused = false;
            lock(_messageQueue)
            {
                foreach (LogMessage message in _tempQueue)
                {
                    _messageQueue.Enqueue(message);
                }
            }
        }

        #endregion
    }
}
