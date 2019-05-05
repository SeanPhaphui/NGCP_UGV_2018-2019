using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Timers;

using MessagePack;
using MessagePack.Resolvers;
using XBee;
using UGV.Core.Navigation;
namespace NGCP.UGV
{
    class UGVXbee
    {
        private double Offset = 0;
        private const int SendRate = 1000; // in milliseconds
        private int MessageId = 0;

        private string VehicleStatus = "disconnected"; // status types: disconnected, ready, waiting, running, paused, error
        private UGV.DriveState UGVState = UGV.DriveState.Idle;
        private UGV.DriveState SavedUGVStateDuringPause;
        private WayPoint NextUGVWaypoint;
        private const int THANOS_GETS_DUSTED = -100;

        private readonly XBeeController Xbee = new XBeeController();
        private XBeeNode ToXbee;

        public event EventHandler<ReceiveConnAckEventArgs> ReceiveConnAck;
        public event EventHandler<ReceiveAddMissionEventArgs> ReceiveAddMission;
        public event EventHandler<ReceivePauseEventArgs> ReceivePause;
        public event EventHandler<ReceiveResumeEventArgs> ReceiveResume;
        public event EventHandler<ReceiveStopEventArgs> ReceiveStop;

        private readonly Dictionary<int, Timer> TimerOutbox = new Dictionary<int, Timer>();
        private readonly Dictionary<int, int> LastReceivedMessageId = new Dictionary<int, int>();

        public Dictionary<int, Timer> Sending = new Dictionary<int, Timer>();
        public Dictionary<int, int> LastSentMessageID = new Dictionary<int, int>();
        public Dictionary<int, ArrayList> Outbox = new Dictionary<int, ArrayList>();


        public UGVXbee(string PortName, int BaudRate, string DestinationMAC)
        {
            InitializeConnection(PortName, BaudRate, DestinationMAC);
        }
        public UGV.DriveState SetUGVState()
        {
            return UGVState;
        }
        public void GetUGVState(UGV.DriveState UGVDriveState)
        {
            SavedUGVStateDuringPause = UGVDriveState;
        }
        public WayPoint AddUGVWaypoint()
        {
            return NextUGVWaypoint;
        }
        public UGV.DriveState ReturnFromPause()
        {
            return SavedUGVStateDuringPause;
        }
        private async void InitializeConnection(string PortName, int BaudRate, string DestinationMAC)
        {
            // Opens Xbee connection
            await Xbee.OpenAsync(PortName, BaudRate);
            ToXbee = await Xbee.GetNodeAsync(new NodeAddress(new LongAddress(UInt64.Parse(DestinationMAC, System.Globalization.NumberStyles.AllowHexSpecifier))));
            // Callback function to whenever we receive a message from the GCS (or other Xbee)
            Xbee.DataReceived += (o, eventArgs) => ReceiveMessage(eventArgs.Data);

            // Tries to connect to the GCS
            SendMessage(0, new ConnectMsg()
            {
                JobsAvailable = new string[] { "ugvRescue" },
            });
        }

        public void SendMessage(int VehicleID, Msg Msg)
        {
            if (Msg == null)
                return;

            Msg.Tid = VehicleID;
            Msg.Sid = 200;
            Msg.Id = MessageId;
            Msg.Time = Time();
            MessageId += 1;
            ArrayList list;


            if (Msg.Type == "ack" || Msg.Type == "badMessage")
            {
                string json;
                byte[] bytes;
                if (Msg.Type == "ack")
                {
                    bytes = MessagePackSerializer.Serialize((AckMsg)Msg);
                    json = MessagePackSerializer.ToJson((AckMsg)Msg);
                    // send ack
                }
                else
                {
                    bytes = MessagePackSerializer.Serialize((BadMsg)Msg);
                    json = MessagePackSerializer.ToJson((BadMsg)Msg);
                }

                Console.WriteLine("Sending {0}", json);
                ToXbee.TransmitDataAsync(bytes);
                return;
            }

            if (Sending.ContainsKey(VehicleID) && Sending[VehicleID] != null)
            {
                if (!Outbox.ContainsKey(VehicleID))
                    list = new ArrayList();
                else
                    list = Outbox[VehicleID];
                list.Add(Msg);
                Outbox[VehicleID] = list;
            }
            else
                SendMessageAsync(Msg);
        }

        /**
         * Rules:
         * 1. Never acknowledge an invalid message. This means the following:
         *    - message with invalid information (missing info, wrong source)
         *    - message sent while vehicle in incorrect state (connectionAck while vehicle is running a mission)
         * 3. Always send SendBadMessage to ALL invalid messages.
         * 4. Same above applies to incrementing last message received ID.
         * 5. Ignore messages of old ID. See following link: https://ground-control-station.readthedocs.io/en/latest/communications/messages/other-messages.html#acknowledgement-message
         *    - however, ALWAYS acknowledge old messages if they are valid, otherwise send BAD MESSAGE
         */

        private void SendMessageAsync(Msg Msg)
        {
            byte[] bytes = new byte[] { };
            string json = "";

            switch (Msg.Type)
            {
                case "connect":
                    bytes = MessagePackSerializer.Serialize((ConnectMsg)Msg);
                    json = MessagePackSerializer.ToJson((ConnectMsg)Msg);
                    break;

                case "update":
                    bytes = MessagePackSerializer.Serialize((UpdateMsg)Msg);
                    json = MessagePackSerializer.ToJson((UpdateMsg)Msg);
                    break;

                case "poi":
                    bytes = MessagePackSerializer.Serialize((POIMsg)Msg);
                    json = MessagePackSerializer.ToJson((POIMsg)Msg);
                    break;

                case "complete":
                    bytes = MessagePackSerializer.Serialize((CompleteMsg)Msg);
                    json = MessagePackSerializer.ToJson((CompleteMsg)Msg);
                    break;

                case "ack":
                    bytes = MessagePackSerializer.Serialize((AckMsg)Msg);
                    json = MessagePackSerializer.ToJson((AckMsg)Msg);
                    break;

                case "badMessage":
                    bytes = MessagePackSerializer.Serialize((BadMsg)Msg);
                    json = MessagePackSerializer.ToJson((BadMsg)Msg);
                    break;

                default:
                    throw new IndexOutOfRangeException("Message type is wrong, cannot send this message");
            }

            Console.WriteLine("Sending {0}", json);
            ToXbee.TransmitDataAsync(bytes);

            // Repeatedly send message if it was not accepted. Transmit above is to send it once,
            // then keep sending it if it was not accepted. An ack message would stop these
            // timers.
            Timer MessageTimer = new Timer(SendRate);
            MessageTimer.Elapsed += (o, eventArgs) => ToXbee.TransmitDataAsync(bytes);

            // Special case to cover connect message
            if (Msg.Type == "connect")
            {
                LastSentMessageID.Add(Msg.Tid, -1);

            }
            else
            {
                LastSentMessageID[Msg.Tid] = Msg.Id;
            }
            Sending[Msg.Tid] = MessageTimer;
            Sending[Msg.Tid].Start();
        }


        private void ReceiveMessage(byte[] Bytes)
        {
            Msg Msg;

            try
            {
                Msg = MessagePackSerializer.Deserialize<Msg>(Bytes);
            }
            catch (Exception)
            {
                Console.WriteLine("Received invalid message {0}", Bytes.ToString());
                return;
            }

            // Ignores vehicles not relevant to NGCP
            if (!IsValidMessageSource(Msg)) return;

            // New message if the ID of this message is larger than the previous message's
            bool NewMessage = LastReceivedMessageId.ContainsKey(Msg.Sid) ? LastReceivedMessageId[Msg.Sid] < Msg.Id : true;

            //Console.WriteLine("Received message of type {0}", Msg.Type);

            switch (Msg.Type)
            {
                case "connectionAck":
                    ConnAckMsg CA = MessagePackSerializer.Deserialize<ConnAckMsg>(Bytes);
                    Console.WriteLine(MessagePackSerializer.ToJson(CA));
                    ProcessConnAckMsg(NewMessage, MessagePackSerializer.Deserialize<ConnAckMsg>(Bytes));
                    break;

                case "start":
                    StartMsg ST = MessagePackSerializer.Deserialize<StartMsg>(Bytes);
                    Console.WriteLine(MessagePackSerializer.ToJson(ST));
                    ProcessStartMsg(NewMessage, MessagePackSerializer.Deserialize<StartMsg>(Bytes));
                    break;

                case "addMission":
                    AddMissionMsg AM = MessagePackSerializer.Deserialize<AddMissionMsg>(Bytes);
                    Console.WriteLine(MessagePackSerializer.ToJson(AM));
                    ProcessAddMissionMsg(NewMessage, MessagePackSerializer.Deserialize<AddMissionMsg>(Bytes));
                    break;

                case "pause":
                    PauseMsg PS = MessagePackSerializer.Deserialize<PauseMsg>(Bytes);
                    Console.WriteLine(MessagePackSerializer.ToJson(PS));
                    ProcessPauseMsg(NewMessage, MessagePackSerializer.Deserialize<PauseMsg>(Bytes));
                    break;

                case "resume":
                    ResumeMsg RS = MessagePackSerializer.Deserialize<ResumeMsg>(Bytes);
                    Console.WriteLine(MessagePackSerializer.ToJson(RS));
                    ProcessResumeMsg(NewMessage, MessagePackSerializer.Deserialize<ResumeMsg>(Bytes));
                    break;

                case "stop":
                    StopMsg SP = MessagePackSerializer.Deserialize<StopMsg>(Bytes);
                    Console.WriteLine(MessagePackSerializer.ToJson(SP));
                    ProcessStopMsg(NewMessage, MessagePackSerializer.Deserialize<StopMsg>(Bytes));
                    break;

                case "ack":
                    AckMsg AK = MessagePackSerializer.Deserialize<AckMsg>(Bytes);
                    Console.WriteLine(MessagePackSerializer.ToJson(AK));
                    ProcessAckMsg(NewMessage, MessagePackSerializer.Deserialize<AckMsg>(Bytes));
                    break;

                case "badMessage":
                    BadMsg BM = MessagePackSerializer.Deserialize<BadMsg>(Bytes);
                    Console.WriteLine(MessagePackSerializer.ToJson(BM));
                    ProcessBadMsg(NewMessage, MessagePackSerializer.Deserialize<BadMsg>(Bytes));
                    break;

                default:
                    SendBadMessage(Msg, "Message type is unrecognized by UGV");
                    break;
            }
        }

        public void ProcessStartMsg(bool NewMessage, StartMsg Msg)
        {
            if (VehicleStatus != "ready")
            {
                SendBadMessage(Msg, "Received start message while status is " + VehicleStatus);
                return;
            }

            if (Msg.JobType != "ugvRescue")
            {
                SendBadMessage(Msg, "Received incorrect jobType " + Msg.JobType + " from start message");
                return;
            }

            // Only process each message once (so if the same message gets sent twice, ignore the second message)
            if (NewMessage)
            {
                LastReceivedMessageId[Msg.Sid] = Msg.Id;

                VehicleStatus = "waiting";
                Console.WriteLine("Starting {0} Mission", Msg.JobType);
            }

            // Acknowledge all valid messages
            SendAcknowledgement(Msg);
        }

        private void ProcessAddMissionMsg(bool NewMessage, AddMissionMsg Msg)
        {
            if (VehicleStatus != "waiting")
            {
                SendBadMessage(Msg, "Received addMission message while status is " + VehicleStatus);
                return;
            }

            if (Msg.MissionInfo.TaskType != "retrieveTarget" && Msg.MissionInfo.TaskType != "deliverTarget")
            {
                SendBadMessage(Msg, "Received incorrect taskType " + Msg.MissionInfo.TaskType + " from addMission message");
                return;
            }

            // Only process each message once (so if the same message gets sent twice, ignore the second message)
            if (NewMessage)
            {
                LastReceivedMessageId[Msg.Sid] = Msg.Id;

                ReceiveAddMissionEventArgs args = new ReceiveAddMissionEventArgs();
                args.Msg = Msg;
                NextUGVWaypoint = new WayPoint(Msg.MissionInfo.Lat, Msg.MissionInfo.Lng, 0);
                UGVState = UGV.DriveState.SearchTarget;
                EventHandler<ReceiveAddMissionEventArgs> handler = ReceiveAddMission;
                handler?.Invoke(this, args);
                VehicleStatus = "running";
                //Console.WriteLine("Starting {0} Task, Lat: {1}, Lng: {2}", Msg.MissionInfo.TaskType, Msg.MissionInfo.Lat, Msg.MissionInfo.Lng);
            }

            // Acknowledge all valid messages
            SendAcknowledgement(Msg);
        }

        private void ProcessPauseMsg(bool NewMessage, PauseMsg Msg)
        {
            // GCS can send pause messages to this vehicle anytime (it sends pause messages to all vehicles
            // even those not related to mission)
            if (VehicleStatus != "running") return;

            if (NewMessage)
            {
                LastReceivedMessageId[Msg.Sid] = Msg.Id;

                ReceivePauseEventArgs args = new ReceivePauseEventArgs();
                args.Msg = Msg;
                EventHandler<ReceivePauseEventArgs> handler = ReceivePause;
                handler?.Invoke(this, args);
                VehicleStatus = "paused";
                Console.WriteLine("Paused task");
            }

            // Acknowledge all valid messages
            SendAcknowledgement(Msg);
        }

        private void ProcessResumeMsg(bool NewMessage, ResumeMsg Msg)
        {
            // GCS can send resume messages to this vehicle anytime (it sends pause messages to all vehicles
            // even those not related to mission)
            if (VehicleStatus != "paused") return;

            // Only process each message once (so if the same message gets sent twice, ignore the second message)
            if (NewMessage)
            {
                LastReceivedMessageId[Msg.Sid] = Msg.Id;

                ReceiveResumeEventArgs args = new ReceiveResumeEventArgs();
                args.Msg = Msg;
                EventHandler<ReceiveResumeEventArgs> handler = ReceiveResume;
                handler?.Invoke(this, args);
                VehicleStatus = "running";
                Console.WriteLine("Resumed task");
            }

            // Acknowledge all valid messages
            SendAcknowledgement(Msg);
        }

        private void ProcessStopMsg(bool NewMessage, StopMsg Msg)
        {
            // GCS can send stop message even if vehicle is ready (usually its because other vehicles arent working right)
            if (VehicleStatus != "waiting" && VehicleStatus != "running" && VehicleStatus != "paused") return;

            if (NewMessage)
            {
                LastReceivedMessageId[Msg.Sid] = Msg.Id;

                ReceiveStopEventArgs args = new ReceiveStopEventArgs();
                args.Msg = Msg;
                EventHandler<ReceiveStopEventArgs> handler = ReceiveStop;
                handler?.Invoke(this, args);
                VehicleStatus = "ready";
                Console.WriteLine("Stopped mission");
            }

            // Acknowledge all valid messages
            SendAcknowledgement(Msg);
        }

        private void ProcessConnAckMsg(bool NewMessage, ConnAckMsg Msg)
        {
            if (!NewMessage) return;
            LastReceivedMessageId[Msg.Sid] = Msg.Id;

            if (!LastSentMessageID.ContainsKey(Msg.Sid) || LastSentMessageID[Msg.Sid] != -1) return;

            Offset = Msg.Time - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            ReceiveConnAckEventArgs args = new ReceiveConnAckEventArgs();
            args.Msg = Msg;
            EventHandler<ReceiveConnAckEventArgs> handler = ReceiveConnAck;
            handler?.Invoke(this, args);
            VehicleStatus = "ready";
            Console.WriteLine("Received connection acknowledgement from GCS, systems are ready");

            IronManDies(Msg.Sid);
        }

        private void ProcessAckMsg(bool NewMessage, AckMsg Msg)
        {
            if (!NewMessage) return;
            LastReceivedMessageId[Msg.Sid] = Msg.Id;

            if (!LastSentMessageID.ContainsKey(Msg.Sid) || LastSentMessageID[Msg.Sid] != Msg.AckId) return;
            
            IronManDies(Msg.Sid);
        }

        private void ProcessBadMsg(bool NewMessage, BadMsg Msg)
        {
            if (!NewMessage) return;
            LastReceivedMessageId[Msg.Sid] = Msg.Id;

            Console.WriteLine("Bad message received {0}", Msg.Error);
        }

        private void IronManDies(int VehicleId)
        {
            Sending[VehicleId].Stop();
            Console.Write("Stopped sending message ");
            if (Outbox.ContainsKey(VehicleId) && Outbox[VehicleId].Count > 0)
            {
                try
                {
                    int outboxSize = Outbox[VehicleId].Count;
                    ArrayList list = Outbox[VehicleId];
                    Msg NextMsg = (Msg)list[0];
                    list.RemoveAt(0);
                    Console.WriteLine("and will send next message, size = {0}", outboxSize);
                    SendMessageAsync(NextMsg);

                    Outbox[VehicleId] = list;
                }
                catch (Exception)
                { }
            }
            else
            {
                Sending[VehicleId] = null;
                Console.WriteLine("and null'd the timer");
            }
        }

        public void SendUpdate(double lat, double lng, double heading)
        {
            SendMessage(0, new UpdateMsg()
            {
                Status = VehicleStatus,
                Lat = lat,
                Lng = lng,
                Heading = heading,
            });
        }

        private void SendAcknowledgement(Msg Msg)
        {
            SendMessage(Msg.Sid, new AckMsg()
            {
                AckId = Msg.Id,
            });
        }

        public void SendUpdates()   // fixture function, do not call unless for testing
        {
            Timer AckTimer = new Timer(750);
            AckTimer.Elapsed += (o, eventArgs) =>
            {

                SendUpdate(0, 0, 0);
            };

            AckTimer.Start();
            
        }

        private void SendBadMessage(Msg Msg, string Error)
        {
            SendMessage(Msg.Sid, new BadMsg()
            {
                Error = Error,
            });
        }

        public void CompleteTask()
        {
            if (VehicleStatus != "paused" && VehicleStatus != "running") return;

            SendMessage(0, new CompleteMsg());
            VehicleStatus = "waiting";
        }

        private double Time()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Offset;
        }

        private bool IsValidMessageSource(Msg Msg)
        {
            return Msg.Sid == 0
                || Msg.Sid == 100
                || Msg.Sid == 300
                || Msg.Sid == 400
                || Msg.Sid == 401
                || Msg.Sid == 500
                || Msg.Sid == 600;
        }
    }

    public class ReceiveConnAckEventArgs : EventArgs
    {
        public ConnAckMsg Msg;
    }

    public class ReceiveAddMissionEventArgs : EventArgs
    {
        public AddMissionMsg Msg;
    }

    public class ReceivePauseEventArgs : EventArgs
    {
        public PauseMsg Msg;
    }

    public class ReceiveResumeEventArgs : EventArgs
    {
        public ResumeMsg Msg;
    }

    public class ReceiveStopEventArgs : EventArgs
    {
        public StopMsg Msg;
    }

}
