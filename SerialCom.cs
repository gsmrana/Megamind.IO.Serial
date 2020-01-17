using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;

namespace Megamind.IO.Serial
{
    public class SerialCom : IDisposable
    {
        #region Data

        private bool _enableRxThread;
        private readonly SerialPort _serialPort;
        private readonly Thread _rxThread;
        private readonly Timer _rxTimer;
        private readonly object _rxQueueLock = new object();
        private readonly Queue<byte[]> _rxQueue = new Queue<byte[]>();
        private readonly AutoResetEvent _rxEvent = new AutoResetEvent(false);

        public delegate void SerialComEventHandler(object sender, SerialComEventArgs e);
        public event SerialComEventHandler OnException;
        public event SerialComEventHandler OnDataReceived;

        #endregion

        #region Properties

        public int RxEventTriggerTimeoutMS { get; set; } = 10;  //milli second
        public int RxEventTriggerBlockSize { get; set; } = 256; //bytes

        #endregion

        #region ctor   

        public SerialCom(string serialPort, int baudRate, bool dtrEnable = false)
        {
            _serialPort = new SerialPort(serialPort, baudRate, Parity.None, 8, StopBits.One)
            {
                DtrEnable = dtrEnable,
                RtsEnable = false,
                Handshake = Handshake.None
            };
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.ErrorReceived += SerialPort_ErrorReceived;

            _rxThread = new Thread(new ThreadStart(ReceiverThread))
            {
                IsBackground = true
            };

            _rxTimer = new Timer(RxEventTimerCallback);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                _rxQueue.Clear();
            _serialPort.Dispose();
            _rxTimer.Dispose();
            _rxEvent.Dispose();
        }

        public void Open()
        {
            _serialPort.Open();
            _enableRxThread = true;
            _rxThread.Start();
        }

        public void Close()
        {
            _enableRxThread = false;
            _rxEvent.Set();
            if (!_rxThread.Join(1000))
                _rxThread.Abort();
            _serialPort.Close();
        }

        #endregion

        #region Public Methods

        public void Write(byte[] bytes)
        {
            _serialPort.Write(bytes, 0, bytes.Length);
        }

        public void Write(string str)
        {
            _serialPort.Write(str);
        }

        #endregion

        #region Event Handlers

        protected virtual void ExceptionLog(string message)
        {
            OnException?.Invoke(this, new SerialComEventArgs { Message = message });
        }

        protected virtual void DataReceived(byte[] data)
        {
            OnDataReceived?.Invoke(this, new SerialComEventArgs { Data = data });
        }

        #endregion

        #region Internal methods

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            ExceptionLog("SerialPort Error: " + e.EventType.ToString());
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (e.EventType != SerialData.Chars) return;
                var data = new byte[_serialPort.BytesToRead];
                _serialPort.Read(data, 0, data.Length);
                lock (_rxQueueLock)
                {
                    _rxQueue.Enqueue(data);
                    if (_rxQueue.Count >= RxEventTriggerBlockSize) _rxEvent.Set();
                }
                _rxTimer.Change(RxEventTriggerTimeoutMS, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                ExceptionLog("DataReceived Error: " + ex.Message);
            }
        }

        private void RxEventTimerCallback(object state)
        {
            try
            {
                _rxEvent.Set();
            }
            catch (Exception ex)
            {
                ExceptionLog("RxEvent Error: " + ex.Message);
            }
        }

        private void ReceiverThread()
        {
            var data = new List<byte>();
            while (_enableRxThread)
            {
                try
                {
                    _rxEvent.WaitOne();
                    lock (_rxQueueLock)
                    {
                        while (_rxQueue.Count > 0)
                            data.AddRange(_rxQueue.Dequeue());
                    }
                    if (data.Count > 0)
                    {
                        DataReceived(data.ToArray());
                        data.Clear();
                    }
                }
                catch (Exception ex)
                {
                    ExceptionLog("Receiver Exception: " + ex.Message);
                }
            }
        }

        #endregion
    }

    public class SerialComEventArgs : EventArgs
    {
        public byte[] Data { get; set; }
        public string Message { get; set; }
    }
}
