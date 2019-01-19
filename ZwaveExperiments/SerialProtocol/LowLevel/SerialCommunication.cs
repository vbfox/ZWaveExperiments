﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace ZwaveExperiments.SerialProtocol.LowLevel
{
    interface ISerialPortWrapper
    {
        int BytesToRead { get; }
        Stream BaseStream { get; }
    }

    class SerialPortWrapper: ISerialPortWrapper
    {
        private readonly SerialPort serialPort;

        public int BytesToRead => serialPort.BytesToRead;

        public Stream BaseStream => serialPort.BaseStream;

        public SerialPortWrapper(SerialPort serialPort)
        {
            this.serialPort = serialPort;
        }
    }

    class SerialCommunication2 : IDisposable
    {
        readonly ISerialPortWrapper serialPort;
        readonly Subject<PooledSerialFrame> messagesReceived = new Subject<PooledSerialFrame>();
        readonly CancellationTokenSource disposeTokenSource = new CancellationTokenSource();
        readonly FrameStream frameStream;
        readonly Task readTask;

        struct TransmitedFrame
        {
        }

        public SerialCommunication2(ISerialPortWrapper serialPort)
        {
            this.serialPort = serialPort ?? throw new ArgumentNullException(nameof(serialPort));

            frameStream = new FrameStream(serialPort.BaseStream);
            readTask = RunReadTask();
        }

        void Ignore(Task task)
        {
            // TODO: Log result
        }

        async Task RunReadTask()
        {
            while (!disposeTokenSource.IsCancellationRequested)
            {
                var newFrame = await frameStream.ReadAsync(disposeTokenSource.Token);

                if (newFrame.Header == FrameHeader.SOF)
                {
                    if (!DataFrame.IsFrameChecksumValid(newFrame.Data))
                    {
                        Ignore(frameStream.WriteAsync(PooledSerialFrame.Nak, CancellationToken.None));
                    }
                    else
                    {
                        Ignore(frameStream.WriteAsync(PooledSerialFrame.Ack, CancellationToken.None));
                    }
                }
            }
        }

        public async Task Write(PooledSerialFrame frame, CancellationToken cancellationToken)
        {

        }

        public void Dispose()
        {
            disposeTokenSource.Cancel();
            readTask.Wait();
            messagesReceived.Dispose();
        }
    }

    class SerialCommunication : ISerialCommunication, IDisposable
    {
        readonly ISerialPortWrapper serialPort;
        readonly Subject<PooledSerialFrame> unsolicitedMessages;
        readonly Task dataPipeTask;
        readonly CancellationTokenSource disposeTokenSource = new CancellationTokenSource();
        readonly FrameStream frameStream;
        readonly SemaphoreSlim operationSemaphore = new SemaphoreSlim(1, 1);
        readonly AutoResetEvent newOperationEvent = new AutoResetEvent(false);
        Operation? currentOperation;

        struct Operation
        {
            public CancellationToken CancellationToken { get; }
            public bool IsQuery { get; }
            public TaskCompletionSource<PooledSerialFrame> CompletionSource { get; }
            public PooledSerialFrame MessageToSend { get; }

            public Operation(bool isQuery, PooledSerialFrame messageToSend, CancellationToken cancellationToken)
            {
                CompletionSource = new TaskCompletionSource<PooledSerialFrame>();
                MessageToSend = messageToSend;
                CancellationToken = cancellationToken;
                IsQuery = isQuery;
            }
        }

        public SerialCommunication(ISerialPortWrapper serialPort)
        {
            this.serialPort = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
            unsolicitedMessages = new Subject<PooledSerialFrame>();

            frameStream = new FrameStream(serialPort.BaseStream);
            dataPipeTask = ProcessStream();
        }

        async Task ProcessStream()
        {
            while (!disposeTokenSource.IsCancellationRequested)
            {
                await newOperationEvent.AsTask();
                Debug.Assert(currentOperation != null, nameof(currentOperation) + " != null");
                var operation = currentOperation.Value;
                currentOperation = null;

                var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(disposeTokenSource.Token, operation.CancellationToken);
                try
                {
                    await frameStream.WriteAsync(operation.MessageToSend, linkedToken.Token);
                    var ackResponse = await frameStream.ReadAsync(linkedToken.Token);
                    if (!operation.IsQuery || ackResponse.Header != FrameHeader.ACK)
                    {
                        operation.CompletionSource.SetResult(ackResponse);
                    }

                    var queryResponse = await frameStream.ReadAsync(linkedToken.Token);
                    operation.CompletionSource.SetResult(queryResponse);
                    if (queryResponse.Header == FrameHeader.SOF)
                    {
                        await frameStream.WriteAsync(PooledSerialFrame.Ack, disposeTokenSource.Token);
                    }
                }
                catch (OperationCanceledException cancel)
                {
                    operation.CompletionSource.SetCanceled();
                    if (cancel.CancellationToken == disposeTokenSource.Token)
                    {
                        throw;
                    }
                }
            }
        }

        public IObservable<PooledSerialFrame> UnsolicitedMessages => unsolicitedMessages;

        public Task WriteAsync(PooledSerialFrame message, CancellationToken cancellationToken = default)
        {
            return OperationAsync(false, message, cancellationToken);
        }

        public Task<PooledSerialFrame> QueryAsync(PooledSerialFrame query, CancellationToken cancellationToken = default)
        {
            return OperationAsync(true, query, cancellationToken);
        }

        async Task<PooledSerialFrame> OperationAsync(bool isQuery, PooledSerialFrame message, CancellationToken cancellationToken)
        {
            await operationSemaphore.WaitAsync(cancellationToken);
            try
            {
                var operation = new Operation(isQuery, message, cancellationToken);
                currentOperation = operation;
                newOperationEvent.Set();
                return await operation.CompletionSource.Task;
            }
            finally
            {
                operationSemaphore.Release();
            }
        }

        public void Dispose()
        {
            disposeTokenSource.Cancel();
            operationSemaphore.Wait();
            operationSemaphore.Dispose();
            dataPipeTask.Wait();
            disposeTokenSource.Dispose();
        }
    }
}