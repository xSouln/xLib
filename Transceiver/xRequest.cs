﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace xLib.Transceiver
{
    public enum ERequestState
    {
        Free,
        Prepare,
        IsTransmit,
        Complite,
        TimeOut,
        ErrorTransmite,
        ErrorTransmiteAction,
        Busy,
        AddError
    }

    public interface ITransmitter
    {
        xAction<bool, byte[]> Transmitter { get; }
    }

    public class xRequestHandler
    {
        protected List<xRequest> requests = new List<xRequest>();
        protected AutoResetEvent read_write_synchronize = new AutoResetEvent(true);
        protected Semaphore queue_size;

        public xRequestHandler(int line_size)
        {
            if (line_size < 1) { line_size = 10; }
            queue_size = new Semaphore(line_size, line_size);
        }

        public xRequestHandler()
        {
            queue_size = new Semaphore(10, 10);
        }

        protected virtual void requests_update()
        {
            int i = 0;
            while (i < requests.Count)
            {
                switch (requests[i].TransmissionState)
                {
                    case ERequestState.Complite: requests[i].Accept(); requests.RemoveAt(i); break;
                    case ERequestState.IsTransmit: i++; break;
                    case ERequestState.Prepare: i++; break;
                    default: requests.RemoveAt(i); break;
                }
            }
        }

        public virtual bool Add(xRequest request)
        {
            read_write_synchronize.WaitOne();
            try
            {
                requests_update();
                if (requests.Count >= 20) { return false; }
                requests.Add(request);
            }
            finally { read_write_synchronize.Set(); }
            return true;
        }

        public virtual void Remove(xRequest request)
        {
            read_write_synchronize.WaitOne();
            try
            {
                for (int i = 0; i < requests.Count; i++)
                {
                    if (requests[i] == request) { requests.RemoveAt(i); }
                    else { i++; }
                }
            }
            finally { read_write_synchronize.Set(); }
        }

        public void LineIn()
        {
            //queue_size.WaitOne();
        }

        public void LineOut()
        {
            //queue_size.Release();
        }

        public bool Identification(xContent content)
        {
            bool result = false;
            read_write_synchronize.WaitOne();
            try
            {
                requests_update();
                for (int i = 0; i < requests.Count; i++)
                {
                    result = requests[i].Response.Identification(content);
                    if (result)
                    {
                        requests[i].Accept();
                        requests.RemoveAt(i);
                        break;
                    }
                }
            }
            finally { read_write_synchronize.Set(); }
            return result;
        }

        public bool Accept(xBuilderBase builder)
        {
            bool result = false;
            read_write_synchronize.WaitOne();
            try
            {
                for (int i = 0; i < requests.Count; i++)
                {
                    result = requests[i].Builder == builder;
                    if (result)
                    {
                        requests[i].Accept();
                        requests.RemoveAt(i);
                        break;
                    }
                }
            }
            finally { read_write_synchronize.Set(); }
            return result;
        }
    }

    public class xRequest
    {
        protected xAction<bool, byte[]> transmitter;
        protected int try_count = 1;
        protected int try_number = 0;
        protected int response_time_out = 100;
        protected long response_time = 0;
        protected Timer timer_transmiter;
        protected AutoResetEvent delay_handler = new AutoResetEvent(false);
        protected volatile ERequestState transmission_state;

        protected AutoResetEvent transmition_synchronize = new AutoResetEvent(true);
        //protected Semaphore sTransmitAction = new Semaphore(1, 1);
        //protected AutoResetEvent transmit_action_synchronize = new AutoResetEvent(true);

        protected volatile bool is_transmit_action;
        protected volatile bool is_accept;
        protected xResponse response;

        public xRequestHandler Handler;

        public byte[] Data;

        public xEvent<string> Tracer;
        public xEvent<xRequest> EventTimeOut;

        public xBuilderBase Builder { get; set; }
        public string Name { get => Builder?.Name; }
        public int ResponseTimeOut { get => response_time_out; }
        public long ResponseTime { get => response_time; }
        public int TryCount { get => try_count; set { if (try_count > 0) { try_count = value; } } }
        public int TryNumber { get => try_number; }
        public xAction<bool, byte[]> Transmitter { get => transmitter; set => transmitter = value; }
        public virtual xResponse Response { get => response; set => response = value; }
        public ERequestState TransmissionState { get => transmission_state; }

        public void Accept()
        {
            transmition_synchronize.WaitOne();
            try
            {
                if (transmission_state == ERequestState.IsTransmit)
                {
                    transmission_state = ERequestState.Complite;
                }
            }
            finally { transmition_synchronize.Set(); }
        }

        protected static void transmit_action(xRequest request)
        {
            //xRequest request = (xRequest)arg;
            request.transmition_synchronize.WaitOne();
            try
            {
                if (request.transmission_state == ERequestState.IsTransmit)
                {
                    if (request.try_number < request.try_count)
                    {
                        if (request.transmitter == null || !request.transmitter(request.Data))
                        {
                            request.transmission_state = ERequestState.ErrorTransmite;
                            request.Builder.Tracer?.Invoke("Transmit: " + request.Builder.Name + " " + request.transmission_state);
                            return;
                        }
                        request.try_number++;
                        request.Builder.Tracer?.Invoke("Transmit: " + request.Builder.Name + " try: " + request.try_number);
                    }
                    else
                    {
                        request.transmission_state = ERequestState.TimeOut;
                        request.Builder.Tracer?.Invoke("TimeOut: " + request.Builder.Name);
                        request.EventTimeOut?.Invoke(request);
                    }
                }
            }
            finally { request.transmition_synchronize.Set(); }
        }

        protected virtual xRequest transmition()
        {
            try
            {
                transmition_synchronize.WaitOne();

                if (transmission_state != ERequestState.Free) { return this; }

                transmission_state = ERequestState.Prepare;
                if (!(bool)Handler?.Add(this)) { transmission_state = ERequestState.Busy; return this; };

                try_number = 0;
                response_time = 0;

                Stopwatch time_transmition = new Stopwatch();
                Stopwatch time_transmit_action = new Stopwatch();

                transmission_state = ERequestState.IsTransmit;
                transmition_synchronize.Set();

                time_transmition.Start();
                do
                {
                    transmit_action(this);
                    time_transmit_action.Restart();
                    while (transmission_state == ERequestState.IsTransmit && time_transmit_action.ElapsedMilliseconds < response_time_out)
                    {
                        Thread.Sleep(1);
                        //await Task.Delay(1);
                    }
                }
                while (transmission_state == ERequestState.IsTransmit);

                time_transmition.Stop();
                time_transmit_action.Stop();
                response_time = time_transmition.ElapsedMilliseconds;
                return this;
            }
            finally { transmition_synchronize.Set(); }
        }

        public virtual void Break()
        {
            try
            {
                transmition_synchronize.WaitOne();
                transmission_state = ERequestState.Free;
                Handler?.Remove(this);
            }
            finally { transmition_synchronize.Set(); }
        }

        public virtual xRequest Transmition(xAction<bool, byte[]> transmitter, int try_count, int response_time_out)
        {
            if (transmitter == null || try_count <= 0 || response_time_out <= 0 || transmission_state != ERequestState.Free) { return null; }
            this.transmitter = transmitter;
            this.try_count = try_count;
            this.response_time_out = response_time_out;
            this.try_number = 0;
            this.response.Result = null;

            var result = transmition();
            return result;
        }

        public virtual async Task<xRequest> TransmitionAsync(xAction<bool, byte[]> transmitter, int try_count, int response_time_out)
        {
            if (transmitter == null || try_count <= 0 || response_time_out <= 0 || transmission_state != ERequestState.Free) { return null; }
            this.transmitter = transmitter;
            this.try_count = try_count;
            this.response_time_out = response_time_out;
            this.try_number = 0;
            this.response.Result = null;

            var result = await Task.Run(() => transmition());
            return result;
        }
    }

    public class xRequest<TResult> : xRequest where TResult : xResponseResult, new()
    {
        public new xResponse<TResult> Response { get => (xResponse<TResult>)response; set => response = value; }

        public TResult Result { get => ((xResponse<TResult>)response).Result; }

        public new xRequest<TResult> Transmition(xAction<bool, byte[]> transmitter, int try_count, int response_time)
        {
            if (transmitter == null || try_count <= 0 || response_time_out <= 0 || transmission_state != ERequestState.Free) { return null; }
            this.transmitter = transmitter;
            this.try_count = try_count;
            this.response_time_out = response_time;
            this.try_number = 0;
            this.response.Result = null;

            var result = transmition();
            return (xRequest<TResult>)result;
        }

        public new async Task<xRequest<TResult>> TransmitionAsync(xAction<bool, byte[]> transmitter, int try_count, int response_time)
        {
            if (transmitter == null || try_count <= 0 || response_time <= 0 || transmission_state != ERequestState.Free) { return null; }
            this.transmitter = transmitter;
            this.try_count = try_count;
            this.response_time_out = response_time;
            this.try_number = 0;
            this.response.Result = null;

            var result = await Task.Run(() => transmition());
            return (xRequest<TResult>)result;
        }
    }
}
