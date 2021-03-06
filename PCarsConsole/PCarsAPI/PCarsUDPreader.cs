﻿
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCarsConsole.PCarsAPI
{
    public class PCarsUDPreader : GameDataReader
    {
        private float rateEstimate = 0;
        private long ticksAtRateEstimateStart = 0;
        private int estimateRateStartPacket = 1000;
        private int estimateRateEndPacket = 2000;
        private int sequenceWrapsAt = 63;
        private Boolean strictPacketOrdering = false;    // when false, out-of-order packets are checked before being discarded

        // we only check the telem packets, not the strings...
        private int lastSequenceNumberForTelemPacket = -1;

        private int telemPacketCount = 0;
        private int totalPacketCount = 0;

        private int discardedTelemCount = 0;
        private int acceptedOutOfSequenceTelemCount = 0;

        private float lastValidTelemCurrentLapTime = -1;
        private float lastValidTelemLapsCompleted = 0;

        private Boolean newSpotterData = true;
        private Boolean running = false;
        private GCHandle handle;
        private Boolean initialised = false;

        private int udpPort = 5606;

        private StructHelper.pCarsAPIStruct workingGameState = new StructHelper.pCarsAPIStruct();
        private StructHelper.pCarsAPIStruct currentGameState = new StructHelper.pCarsAPIStruct();
        //private StructHelper.pCarsAPIStruct previousGameState = new StructHelper.pCarsAPIStruct();

        private const int sParticipantInfoStrings_PacketSize = 1347;
        private const int sParticipantInfoStringsAdditional_PacketSize = 1028;
        private const int sTelemetryData_PacketSize = 1367;

        private byte[] receivedDataBuffer;

        private IPEndPoint broadcastAddress;
        private UdpClient udpClient;


        /*
        private static Boolean[] buttonsState = new Boolean[8];

        public static Boolean getButtonState(int index)
        {
            return buttonsState[index];
        }
        */

        protected override Boolean InitialiseInternal()
        {
            if (!this.initialised)
            {
                workingGameState.mVersion = 5;
                currentGameState.mVersion = 5;
     //           previousGameState.mVersion = 5;
                acceptedOutOfSequenceTelemCount = 0;
                discardedTelemCount = 0;
                telemPacketCount = 0;
                totalPacketCount = 0;
                lastValidTelemCurrentLapTime = -1;
                lastValidTelemLapsCompleted = 0;
                rateEstimate = 0;
                ticksAtRateEstimateStart = -1;
             //   if (dumpToFile)
            //    {
             //       dataToDump = new List<PCarsSharedMemoryReader.PCarsStructWrapper>();
             //   }
                this.broadcastAddress = new IPEndPoint(IPAddress.Any, udpPort);
                this.udpClient = new UdpClient();
                this.udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                this.udpClient.ExclusiveAddressUse = false; // only if you want to send/receive on same machine.
                this.udpClient.Client.Bind(this.broadcastAddress);
                this.receivedDataBuffer = new byte[this.udpClient.Client.ReceiveBufferSize];
                this.running = true;
                this.udpClient.Client.BeginReceive(this.receivedDataBuffer, 0, this.receivedDataBuffer.Length, SocketFlags.None, ReceiveCallback, this.udpClient.Client);
                this.initialised = true;
                Console.WriteLine("Listening for UDP data on port " + udpPort);                
            }
            return this.initialised;
        }

        private void ReceiveCallback(IAsyncResult result)
        {
            //Socket was the passed in as the state
            try
            {
                Socket socket = (Socket)result.AsyncState;
                int received = socket.EndReceive(result);
                if (received > 0)
                {
                    // do something with the data
                    lock (this)
                    {
                        try
                        {
                            readFromOffset(0, this.receivedDataBuffer);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error reading UDP data ", e.Message);
                        }
                    }
                }
                if (running)
                {
                    socket.BeginReceive(this.receivedDataBuffer, 0, this.receivedDataBuffer.Length, SocketFlags.None, new AsyncCallback(ReceiveCallback), socket);
                }
            }
            catch (Exception e)
            {
                this.initialised = false;
                if (e is ObjectDisposedException || e is SocketException)
                {
                    Console.WriteLine("Socket is closed");                    
                    return;
                }
                throw;
            }
        }


        
        public override Object ReadGameData()
        {
            PCarsStructWrapper structWrapper = new PCarsStructWrapper();
            structWrapper.ticksWhenRead = DateTime.Now.Ticks;
            lock (this)
            {
                if (!initialised)
                {
                    if (!InitialiseInternal())
                    {
                        throw new GameDataReadException("Failed to initialise UDP client");
                    }
                }
                //previousGameState = StructHelper.Clone(currentGameState);
                currentGameState = StructHelper.Clone(workingGameState);
            
            }
            structWrapper.data = currentGameState;

            return structWrapper;
        }

    

        private int readFromOffset(int offset, byte[] rawData)
        {
            totalPacketCount++;
            if (totalPacketCount == estimateRateStartPacket)
            {
                ticksAtRateEstimateStart = DateTime.Now.Ticks;
            }
            else if (totalPacketCount == estimateRateEndPacket && ticksAtRateEstimateStart > 0)
            {
                rateEstimate = (float)(TimeSpan.TicksPerSecond * (estimateRateEndPacket - estimateRateStartPacket)) / (float)(DateTime.Now.Ticks - ticksAtRateEstimateStart);
            }
            // the first 2 bytes are the version - discard it for now
            int frameTypeAndSequence = rawData[offset + 2];
            int frameType = frameTypeAndSequence & 3;
            int sequence = frameTypeAndSequence >> 2;
            int frameLength = 0;
            if (frameType == 0)
            {
                telemPacketCount++;
                frameLength = sTelemetryData_PacketSize;
                Boolean sequenceCheckOK = isNextInSequence(sequence);
                if (strictPacketOrdering && !sequenceCheckOK)
                {
                    discardedTelemCount++;
                }
                else
                {
                    handle = GCHandle.Alloc(rawData.Skip(offset).Take(frameLength).ToArray(), GCHandleType.Pinned);
                    sTelemetryData telem = (sTelemetryData)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(sTelemetryData));
                    if (sequenceCheckOK || !telemIsOutOfSequence(telem))
                    {
                        //buttonsState = ConvertByteToBoolArray(telem.sDPad);
                        lastSequenceNumberForTelemPacket = sequence;
                        workingGameState = StructHelper.MergeWithExistingState(workingGameState, telem);
                        newSpotterData = workingGameState.hasNewPositionData;
                        handle.Free();
                    }
                }    
            }
            else if (frameType == 1)
            {
                frameLength = sParticipantInfoStrings_PacketSize;
                handle = GCHandle.Alloc(rawData.Skip(offset).Take(frameLength).ToArray(), GCHandleType.Pinned);
                sParticipantInfoStrings strings = (sParticipantInfoStrings)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(sParticipantInfoStrings));
                workingGameState = StructHelper.MergeWithExistingState(workingGameState, strings);
                handle.Free();
            }
            else if (frameType == 2)
            {
                frameLength = sParticipantInfoStringsAdditional_PacketSize;
                handle = GCHandle.Alloc(rawData.Skip(offset).Take(frameLength).ToArray(), GCHandleType.Pinned);
                sParticipantInfoStringsAdditional additional = (sParticipantInfoStringsAdditional)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(sParticipantInfoStringsAdditional));
                workingGameState = StructHelper.MergeWithExistingState(workingGameState, additional);
                handle.Free();
            }
            return frameLength + offset;
        }

        private Boolean isNextInSequence(int thisPacketSequenceNumber)
        {
            if (lastSequenceNumberForTelemPacket != -1)
            {
                int expected = lastSequenceNumberForTelemPacket + 1;
                if (expected > sequenceWrapsAt)
                {
                    expected = 0;
                }
                if (expected != thisPacketSequenceNumber)
                {
                    return false;
                }
            }
            return true;
        }

        private Boolean telemIsOutOfSequence(sTelemetryData telem)
        {
            if (telem.sViewedParticipantIndex >= 0 && telem.sParticipantInfo.Length > telem.sViewedParticipantIndex)
            {
                int lapsCompletedInTelem = telem.sParticipantInfo[telem.sViewedParticipantIndex].sLapsCompleted;
                float lapTimeInTelem = telem.sCurrentTime;
                if (lapTimeInTelem > 0 && lastValidTelemCurrentLapTime > 0)
                {
                    // if the number of completed laps has decreased, or our laptime has decreased without starting
                    // a new lap then we need to discard the packet. The lapsCompleted is unreliable, this may end badly
                    if (lastValidTelemLapsCompleted > lapsCompletedInTelem ||
                        (lapTimeInTelem < lastValidTelemCurrentLapTime && lastValidTelemLapsCompleted == lapsCompletedInTelem))
                    {
                        discardedTelemCount++;
                        return true;
                    }
                }
                lastValidTelemCurrentLapTime = lapTimeInTelem;
                lastValidTelemLapsCompleted = lapsCompletedInTelem;
                acceptedOutOfSequenceTelemCount++;
            }            
            return false;
        }
    
        public override void Dispose()
        {
            if (udpClient != null)
            {
                stop();
                udpClient.Close();
            }
        }


        public override void stop()
        {
            running = false;
            if (udpClient != null && udpClient.Client != null && udpClient.Client.Connected)
            {
                udpClient.Client.Disconnect(true);
            }
            Console.WriteLine("Stopped UDP data receiver, received " + telemPacketCount + 
                " telem packets, accepted " + acceptedOutOfSequenceTelemCount + " out-of-sequence packets, discarded " + discardedTelemCount + " packets");
            if (rateEstimate > 0) 
            {
                Console.WriteLine("Received " + totalPacketCount + " total packets at an estimated rate of " + rateEstimate + "Hz");
            }
            this.initialised = false;
            acceptedOutOfSequenceTelemCount = 0;
            discardedTelemCount = 0;
            telemPacketCount = 0;
            totalPacketCount = 0;
            ticksAtRateEstimateStart = -1;
            lastValidTelemCurrentLapTime = -1;
            lastValidTelemLapsCompleted = 0; 
            rateEstimate = 0;
           // buttonsState = new Boolean[8];
        }

        /*
        public int getButtonIndexForAssignment()
        {
            Boolean isAlreadyRunning = this.initialised;
            if (!isAlreadyRunning)
            {
                InitialiseInternal();
            }
            int pressedIndex = -1;
            DateTime timeout = DateTime.Now.Add(TimeSpan.FromSeconds(10));
            while (pressedIndex == -1 && DateTime.Now < timeout)
            {
                for (int i = 0; i < buttonsState.Count(); i++)
                {
                    if (buttonsState[i])
                    {
                        pressedIndex = i;
                        break;
                    }
                }
            }
            if (!isAlreadyRunning)
            {
                udpClient.Close();
                this.initialised = false;
            }
            buttonsState = new Boolean[8];
            initialised = false;
            return pressedIndex;
        }

    */

        public static bool[] ConvertByteToBoolArray(byte b)
        {
            bool[] result = new bool[8];
            // check each bit in the byte. if 1 set to true, if 0 set to false
            for (int i = 0; i < 8; i++)
            {
                result[i] = (b & (1 << i)) == 0 ? false : true;
            }
            // reverse the array?
            Array.Reverse(result);
            return result;
        }
    }
}
