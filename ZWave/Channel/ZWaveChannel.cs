using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZWave.Channel.Protocol;

namespace ZWave.Channel
{
    public class ZWaveChannel
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly Task _portReadTask;
        private readonly Task _processEventsTask;
        private readonly Task _transmitTask;
        private readonly BlockingCollection<NodeEvent> _eventQueue = new BlockingCollection<NodeEvent>();
        private readonly BlockingCollection<Message> _transmitQueue = new BlockingCollection<Message>();
        private readonly BlockingCollection<Message> _responseQueue = new BlockingCollection<Message>();

        public readonly ISerialPort Port;
        public TextWriter Log { get; set; }
        public TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(2);
        public TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);
        public event EventHandler<NodeEventArgs> NodeEventReceived;
        public event EventHandler<LogEventArgs> LogEventReceived;
        public event EventHandler<ErrorEventArgs> Error;

        public ZWaveChannel(ISerialPort port)
        {
            if ((Port = port) == null)
                throw new ArgumentNullException(nameof(port));

            _semaphore = new SemaphoreSlim(1, 1);
            _processEventsTask = new Task(() => ProcessQueue(_eventQueue, OnNodeEventReceived));
            _transmitTask = new Task(() => ProcessQueue(_transmitQueue, OnTransmit));
            _portReadTask = new Task(() => ReadPort(Port));
        }

#if NET || WINDOWS_UWP
        public ZWaveChannel(string portName)
             : this(new SerialPort(portName))
        {
        }
#endif

#if WINDOWS_UWP
        public ZWaveChannel(ushort vendorId, ushort productId)
             : this(new SerialPort(vendorId, productId))
        {
        }
#endif

        protected virtual void OnError(ErrorEventArgs e)
        {
            LogMessage($"Exception: {e.Error}");
            Error?.Invoke(this, e);
        }

        private void LogMessage(string message)
        {
            if (Log != null && message != null)
            {
                Log.WriteLine(message);
            }
            Debug.WriteLine(message);
            LogEventReceived?.Invoke(this, new LogEventArgs(message));
        }

        private void HandleException(Exception ex)
        {
            if (ex is AggregateException)
            {
                foreach (var inner in ((AggregateException)ex).InnerExceptions)
                {
                    LogMessage(inner.ToString());
                }
                return;
            }
            LogMessage(ex.ToString());
        }

        byte[] ReadAppendBuffer = null;

        private async void ReadPort(ISerialPort port)
        {
            if (port == null)
                throw new ArgumentNullException(nameof(port));




            //await port.InputStream.ReadAsyncExact(buffer, 0, 1);

            while (true)
            {
                try
                {
                    // we are going to start reading Serial from here.

                    var br = await port.InputStream.LoadAsync(1024);
                    byte[] buffer = new byte[br];
                    port.InputStream.ReadBytes(buffer);
                    if (ReadAppendBuffer != null)
                        buffer = ReadAppendBuffer.Concat(buffer).ToArray();

                    if (br > 0)
                    {
                        // print buffer

                        Debug.WriteLine("Recv Buff: " + Message.ByteArrayToString(buffer, buffer.Length));
                        // let's find out if it's valid.
                        FrameHeader header = FrameHeader.TMO;

                        while (buffer != null && buffer.Length > 0)
                        {


                            try
                            {
                                header = (FrameHeader)buffer[0];
                            }
                            catch (Exception ex)
                            {
                                // Frame out of Sync..

                                // Let's ignore and move the fuck on for now.
                                Debug.WriteLine(ex.ToString());
                                continue;
                            }
                            if (header == FrameHeader.SOF)
                            {

                                byte length = buffer[1];
                                var type = (MessageType)buffer[2];
                                var function = (Function)buffer[3];
                                var payload = buffer.Skip(4).Take(length - 3).ToArray();

                                if (buffer.Skip(1).Take(length).Aggregate((byte)0xFF, (total, next) => (byte)(total ^ next)) != buffer[length + 1])
                                    throw new ChecksumException("Checksum failure");
                                // wait for message received (blocking)
                                var message = Message.Read(header, length, type, function, payload);
                                LogMessage($"Received: {message}");

                                // ignore ACK, no processing of ACK needed
                                if (message == Message.ACK)
                                {
                                    buffer = buffer.Skip(length + 1).ToArray();
                                    continue;
                                }

                                // is it a eventmessage from a node?
                                if (message is NodeEvent)
                                {
                                    // yes, so add to eventqueue
                                    _eventQueue.Add((NodeEvent)message);
                                    // send ACK to controller
                                    _transmitQueue.Add(Message.ACK);
                                    // we're done
                                    buffer = buffer.Skip(length + 1).ToArray();
                                    continue;
                                }

                                // not a event, so it's a response to a request
                                _responseQueue.Add(message);
                                // send ACK to controller
                                _transmitQueue.Add(Message.ACK);
                                buffer = buffer.Skip(length + 1).ToArray();
                            }
                            else
                            {
                                buffer = buffer.Skip(1).ToArray();
                                if (header == FrameHeader.ACK)
                                    continue;
                                // need to retransmit.
                                if (header == FrameHeader.NAK)
                                {
                                    _responseQueue.Add(Message.NAK);
                                }
                                if (header == FrameHeader.CAN)
                                {
                                    _responseQueue.Add(Message.CAN);
                                }
                                    


                            }
                        }



                    }
                    else
                    {
                        // nothing happened in 1 second..  Something wrong?
                        Debug.WriteLine("Serial Port 1 second timeout on Read");
                    }






                }
                catch (ChecksumException ex)
                {
                    LogMessage($"Exception: {ex}");
                    _transmitQueue.Add(Message.NAK);
                }
                catch (UnknownFrameException ex)
                {
                    // probably out of sync on the serial port
                    // ToDo: handle gracefully 
                    Debug.WriteLine(ex);
                    //OnError(new ErrorEventArgs(ex));
                }
                catch (IOException)
                {
                    // port closed, we're done so return
                    return;
                }
                catch (Exception ex)
                {
                    // just raise error event. don't break reading of serial port
                    OnError(new ErrorEventArgs(ex));
                }
            }
        }

        private void ProcessQueue<T>(BlockingCollection<T> queue, Action<T> process) where T : Message
        {
            if (queue == null)
                throw new ArgumentNullException(nameof(queue));
            if (process == null)
                throw new ArgumentNullException(nameof(process));

            while (!queue.IsCompleted)
            {
                var message = default(Message);
                try
                {
                    message = queue.Take();
                }
                catch (InvalidOperationException)
                {
                }

                if (message != null)
                {
                    process((T)message);
                }
            }
        }

        private void OnNodeEventReceived(NodeEvent nodeEvent)
        {
            if (nodeEvent == null)
                throw new ArgumentNullException(nameof(nodeEvent));

            var handler = NodeEventReceived;
            if (handler != null)
            {
                foreach (var invocation in handler.GetInvocationList().Cast<EventHandler<NodeEventArgs>>())
                {
                    try
                    {
                        invocation(this, new NodeEventArgs(nodeEvent.NodeID, nodeEvent.Command));
                    }
                    catch (Exception ex)
                    {
                        LogMessage(ex.ToString());
                    }
                }
            }
        }

        private void OnTransmit(Message message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            //message.Write(Port.OutputStream);
            message.Write(Port.OutputStream).ConfigureAwait(false);
            LogMessage($"Transmitted: {message}");
        }

        private async Task<Message> WaitForResponse(Func<Message, bool> predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            while (true)
            {
                var result = await Task.Run((Func<Message>)(() =>
                {
                    var message = default(Message);
                    _responseQueue.TryTake(out message, ResponseTimeout);
                    return message;
                })).ConfigureAwait(false);

                if (result == null)
                    throw new TimeoutException();
                if (result == Message.NAK)
                    throw new NakResponseException();
                if (result == Message.CAN)
                    throw new CanResponseException();
                if (result is NodeCommandCompleted && ((NodeCommandCompleted)result).TransmissionState != TransmissionState.CompleteOk)
                    throw new TransmissionException($"Transmission failure: {((NodeCommandCompleted)result).TransmissionState}.");

                if (predicate(result))
                {
                    return result;
                }
            }
        }

        public void Open()
        {

            Port.Open();



            _portReadTask.Start();
            _processEventsTask.Start();
            _transmitTask.Start();
        }

        public void Close()
        {
            Port.Close();

            _eventQueue.CompleteAdding();
            _responseQueue.CompleteAdding();
            _transmitQueue.CompleteAdding();

            _portReadTask.Wait();
            _processEventsTask.Wait();
            _transmitTask.Wait();
        }

        private async Task<Byte[]> Exchange(Func<Task<Byte[]>> func, string message)
        {
            if (func == null)
                throw new ArgumentNullException(nameof(func));

            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var attempt = 0;
                while (true)
                {
                    try
                    {
                        return await func().ConfigureAwait(false);
                    }
                    catch (CanResponseException)
                    {
                        if (attempt++ >= 3)
                            throw;

                        LogMessage($"CAN received on: {message}. Retrying attempt: {attempt}");

                        await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                    }
                    catch (TransmissionException)
                    {
                        if (attempt++ >= 3)
                            throw;

                        LogMessage($"Transmission failure on: {message}. Retrying attempt: {attempt}");

                        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        if (attempt++ >= 3)
                            throw;

                        LogMessage($"Timeout on: {message}. Retrying attempt: {attempt}");

                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public Task<byte[]> Send(Function function, params byte[] payload)
        {

            return Exchange(async () =>
            {
                var request = new ControllerFunction(function, payload);
                _transmitQueue.Add(request);

                var response = await WaitForResponse((message) =>
                {
                    return (message is ControllerFunctionCompleted && ((ControllerFunctionCompleted)message).Function == function);
                }).ConfigureAwait(false);

                return ((ControllerFunctionCompleted)response).Payload;
            }, $"{function} {(payload != null ? BitConverter.ToString(payload) : string.Empty)}");
        }

        public Task Send(byte nodeID, Command command)
        {
            if (nodeID == 0)
                throw new ArgumentOutOfRangeException(nameof(nodeID), nodeID, "nodeID can not be 0");
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            return Exchange(async () =>
            {
                var request = new NodeCommand(nodeID, command);
                _transmitQueue.Add(request);

                await WaitForResponse((message) =>
                {
                    return (message is NodeCommandCompleted && ((NodeCommandCompleted)message).CallbackID == request.CallbackID);
                }).ConfigureAwait(false);

                return null;
            }, $"NodeID:{nodeID:D3}, Command:{command}");
        }

        public Task<Byte[]> Send(byte nodeID, Command command, byte responseCommandID)
        {
            if (nodeID == 0)
                throw new ArgumentOutOfRangeException(nameof(nodeID), nodeID, "nodeID can not be 0");
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            return Exchange(async () =>
            {
                var completionSource = new TaskCompletionSource<Command>();

                EventHandler<NodeEventArgs> onNodeEventReceived = (_, e) =>
                {
                    if (e.NodeID == nodeID && e.Command.ClassID == command.ClassID && e.Command.CommandID == responseCommandID)
                    {
                        // BugFix: 
                        // http://stackoverflow.com/questions/19481964/calling-taskcompletionsource-setresult-in-a-non-blocking-manner
                        Task.Run(() => completionSource.TrySetResult(e.Command));
                    }
                };

                var request = new NodeCommand(nodeID, command);
                _transmitQueue.Add(request);

                NodeEventReceived += onNodeEventReceived;
                try
                {
                    await WaitForResponse((message) =>
                    {
                        return (message is NodeCommandCompleted && ((NodeCommandCompleted)message).CallbackID == request.CallbackID);
                    }).ConfigureAwait(false);

                    try
                    {
                        using (var cancellationTokenSource = new CancellationTokenSource())
                        {
                            cancellationTokenSource.CancelAfter(ResponseTimeout);
                            cancellationTokenSource.Token.Register(() => completionSource.TrySetCanceled(), useSynchronizationContext: false);

                            var response = await completionSource.Task.ConfigureAwait(false);
                            return response.Payload;
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        throw new TimeoutException();
                    }
                }
                finally
                {
                    NodeEventReceived -= onNodeEventReceived;
                }
            }, $"NodeID:{nodeID:D3}, Command:[{command}], Reponse:{responseCommandID}");
        }
    }
}
