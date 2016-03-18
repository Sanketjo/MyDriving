﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using System.Threading;

namespace ObdLibUWP
{
    public class ObdWrapper
    {
        const uint BufSize = 64;
        const int Interval = 100;
        const string DefValue = "-255";
        private StreamSocket _socket = null;
        private RfcommDeviceService _service = null;
        private DataReader dataReaderObject = null;
        private DataWriter dataWriterObject = null;
        private bool _connected = true;
        private Dictionary<string, string> _data = null;
        private bool _running = true;
        private Object _lock = new Object();
        private bool _simulatormode;
        private Dictionary<string, string> _PIDs;

        public async Task<bool> Init(bool simulatormode = false)
        {
            this._running = true;
            //initialize _data
            this._data = new Dictionary<string, string>();
            this._data.Add("vin", DefValue);  //VIN
            _PIDs = ObdShare.ObdUtil.GetPIDs();
            foreach (var v in _PIDs.Values)
            {
                this._data.Add(v, DefValue);
            }

            _simulatormode = simulatormode;
            if (simulatormode)
            {
                PollObd();

                ////these code is for testing.
                //while (true)
                //{
                //    await Task.Delay(2000);
                //    var dse = Read();
                //}

                return true;
            }

            DeviceInformationCollection DeviceInfoCollection = await DeviceInformation.FindAllAsync(RfcommDeviceService.GetDeviceSelector(RfcommServiceId.SerialPort));
            var numDevices = DeviceInfoCollection.Count();
            DeviceInformation device = null;
            foreach (DeviceInformation info in DeviceInfoCollection)
            {
                if (info.Name.ToLower().Contains("obd"))
                {
                    device = info;
                }
            }
            if (device == null)
                return false;
            try
            {
                _service = await RfcommDeviceService.FromIdAsync(device.Id);

                if (_socket != null)
                {
                    // Disposing the socket with close it and release all resources associated with the socket
                    _socket.Dispose();
                }

                _socket = new StreamSocket();
                try
                {
                    // Note: If either parameter is null or empty, the call will throw an exception
                    await _socket.ConnectAsync(_service.ConnectionHostName, _service.ConnectionServiceName);
                    _connected = true;
                }
                catch (Exception ex)
                {
                    this._connected = false;
                    System.Diagnostics.Debug.WriteLine("Connect:" + ex.Message);
                }
                // If the connection was successful, the RemoteAddress field will be populated
                if (this._connected)
                {
                    string msg = String.Format("Connected to {0}!", _socket.Information.RemoteAddress.DisplayName);
                    System.Diagnostics.Debug.WriteLine(msg);

                    dataWriterObject = new DataWriter(_socket.OutputStream);
                    dataReaderObject = new DataReader(_socket.InputStream);

                    //initialize the device
                    string s;
                    s = await SendAndReceive("ATZ\r");
                    s = await SendAndReceive("ATE0\r");
                    s = await SendAndReceive("ATL1\r");
                    //s = await SendAndReceive("0100\r");
                    s = await SendAndReceive("ATSP00\r");

                    PollObd();

                    ////these code is for testing.
                    //while (true)
                    //{
                    //    await Task.Delay(2000);
                    //    var dse = Read();
                    //}

                    return true;
                }
                else
                    return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Overall Connect: " + ex.Message);
                if (dataReaderObject != null)
                {
                    dataReaderObject.Dispose();
                    dataReaderObject = null;
                }
                if (dataWriterObject != null)
                {
                    dataWriterObject.Dispose();
                    dataWriterObject = null;
                }
                if (this._socket != null)
                {
                    await this._socket.CancelIOAsync();
                    _socket.Dispose();
                    _socket = null;
                }
                return false;
            }
        }

        private async void PollObd()
        {
            try
            {
                string s;
                if (this._simulatormode)
                    s = "SIMULATORWINPHONE";
                else
                    s = await GetVIN();
                lock (_lock)
                {
                    _data["vin"] = s;
                }
                while (true)
                {
                    foreach (var cmd in _PIDs.Keys)
                    {
                        var key = _PIDs[cmd];
                        if (_simulatormode)
                            s = ObdShare.ObdUtil.GetEmulatorValue(cmd);
                        else
                            s = await RunCmd(cmd);
                        if (s != "ERROR")
                            lock (_lock)
                            {
                                _data[key] = s;
                            }
                        if (!this._running)
                            return;
                        await Task.Delay(Interval);
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
                _running = false;
                if (dataReaderObject != null)
                {
                    dataReaderObject.Dispose();
                    dataReaderObject = null;
                }
                if (dataWriterObject != null)
                {
                    dataWriterObject.Dispose();
                    dataWriterObject = null;
                }
                if (this._socket != null)
                {
                    await this._socket.CancelIOAsync();
                    _socket.Dispose();
                    _socket = null;
                }
            }
        }

        private async Task<string> ReadAsync()
        {
            string ret = await ReadAsyncRaw();
            while (!ret.Trim().EndsWith(">"))
            {
                string tmp = await ReadAsyncRaw();
                ret = ret + tmp;
            }
            return ret;
        }

        public async Task<string> GetSpeed()
        {
            if (_simulatormode)
            {
                var r = new Random();
                return r.Next().ToString();
            }
            string result;
            result = await SendAndReceive("010D\r");
            return ObdShare.ObdUtil.ParseObd01Msg(result);
        }
        public async Task<string> GetVIN()
        {
            string result;
            result = await SendAndReceive("0902\r");
            if (result.StartsWith("49"))
            {
                while (!result.Contains("49 02 05"))
                {
                    string tmp = await ReadAsync();
                    result += tmp;
                }
            }
            return ObdShare.ObdUtil.ParseVINMsg(result);
        }
        public Dictionary<string, string> Read()
        {
            if (!this._simulatormode && this._socket == null)
            {
                //if there is no connection
                return null;
            }
            var ret = new Dictionary<string, string>();
            lock (_lock)
            {
                foreach (var key in _data.Keys)
                {
                    ret.Add(key, _data[key]);
                }
                foreach (var v in _PIDs.Values)
                {
                    this._data[v] = DefValue;
                }
            }
            return ret;
        }

        private async Task<string> SendAndReceive(string msg)
        {
            await WriteAsync(msg);
            string s = await ReadAsync();
            System.Diagnostics.Debug.WriteLine("Received: " + s);
            s = s.Replace("SEARCHING...\r\n", "");
            return s;
        }

        private async void Send(string msg)
        {
            try
            {
                if (_socket.OutputStream != null)
                {
                    //Launch the WriteAsync task to perform the write
                    await WriteAsync(msg);
                }
                else
                {
                    //status.Text = "Select a device and connect";
                }
            }
            catch (Exception ex)
            {
                //status.Text = "Send(): " + ex.Message;
                System.Diagnostics.Debug.WriteLine("Send(): " + ex.Message);
            }
            finally
            {
                // Cleanup once complete
                if (dataWriterObject != null)
                {
                    dataWriterObject.DetachStream();
                    dataWriterObject = null;
                }
            }
        }

        private async Task WriteAsync(string msg)
        {
            Task<UInt32> storeAsyncTask;

            if (msg.Length != 0)
            {
                // Load the text from the sendText input text box to the dataWriter object
                dataWriterObject.WriteString(msg);

                // Launch an async task to complete the write operation
                storeAsyncTask = dataWriterObject.StoreAsync().AsTask();

                UInt32 bytesWritten = await storeAsyncTask;
                if (bytesWritten > 0)
                {
                    string status_Text = msg + ", ";
                    status_Text += bytesWritten.ToString();
                    status_Text += " bytes written successfully!";
                    System.Diagnostics.Debug.WriteLine(status_Text);
                }
            }
        }

        private async Task<string> ReadAsyncRaw()
        {
            Task<UInt32> loadAsyncTask;
            uint ReadBufferLength = 1024;

            // Set InputStreamOptions to complete the asynchronous read operation when one or more bytes is available
            dataReaderObject.InputStreamOptions = InputStreamOptions.Partial;

            // Create a task object to wait for data on the serialPort.InputStream
            loadAsyncTask = dataReaderObject.LoadAsync(ReadBufferLength).AsTask();

            // Launch the task and wait
            UInt32 bytesRead = await loadAsyncTask;
            if (bytesRead > 0)
            {
                try
                {
                    string recvdtxt = dataReaderObject.ReadString(bytesRead);
                    System.Diagnostics.Debug.WriteLine(recvdtxt);
                    return recvdtxt;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ReadAsync: " + ex.Message);
                    return "";
                }
            }
            return "";
        }
        private async Task<string> RunCmd(string cmd)
        {
            string result;
            result = await SendAndReceive(cmd + "\r");
            return ObdShare.ObdUtil.ParseObd01Msg(result);
        }
        public async Task Disconnect()
        {
            _running = false;
            if (dataReaderObject != null)
            {
                dataReaderObject.Dispose();
                dataReaderObject = null;
            }
            if (dataWriterObject != null)
            {
                dataWriterObject.Dispose();
                dataWriterObject = null;
            }
            if (this._socket != null)
            {
                try
                {
                    await this._socket.CancelIOAsync();
                    _socket.Dispose();
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }
                _socket = null;
            }
        }
    }
}