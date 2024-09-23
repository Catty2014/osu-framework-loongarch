// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using FFmpeg.AutoGen;
using osuTK;
using osu.Framework.Graphics.Textures;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.EnumExtensions;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Platform.Linux.Native;
using System.Buffers;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Represents a video decoder that can be used convert video streams and files into textures.
    /// </summary>
    public unsafe class VideoDecoder : IDisposable
    {
        /// <summary>
        /// The duration of the video that is being decoded. Can only be queried after the decoder has started decoding has loaded. This value may be an estimate by FFmpeg, depending on the video loaded.
        /// </summary>
        public double Duration { get; private set; }

        /// <summary>
        /// True if the decoder currently does not decode any more frames, false otherwise.
        /// </summary>
        public bool IsRunning => State == DecoderState.Running;

        /// <summary>
        /// True if the decoder has faulted after starting to decode. You can try to restart a failed decoder by invoking <see cref="StartDecoding"/> again.
        /// </summary>
        public bool IsFaulted => State == DecoderState.Faulted;

        /// <summary>
        /// The timestamp of the last frame that was decoded by this video decoder, or 0 if no frames have been decoded.
        /// </summary>
        public float LastDecodedFrameTime => lastDecodedFrameTime;

        /// <summary>
        /// The frame rate of the video stream this decoder is decoding.
        /// </summary>
        public double FrameRate => videoStream == null ? 0 : videoStream->avg_frame_rate.GetValue();

        /// <summary>
        /// True if the decoder can seek, false otherwise. Determined by the stream this decoder was created with.
        /// </summary>
        public bool CanSeek => dataStream?.CanSeek == true;

        /// <summary>
        /// The current state of the decoding process.
        /// </summary>
        public DecoderState State { get; private set; }

        /// <summary>
        /// Determines which hardware acceleration device(s) should be used.
        /// </summary>
        public readonly Bindable<HardwareVideoDecoder> TargetHardwareVideoDecoders = new Bindable<HardwareVideoDecoder>();

        // libav-context-related
        private AVFormatContext* formatContext;
        private AVIOContext* ioContext;

        private AVStream* videoStream;
        private AVCodecContext* videoCodecContext;
        private SwsContext* swsContext;

        private AVStream* audioStream;
        private AVCodecContext* audioCodecContext => audioStream->codec;
        private SwrContext* swrContext;

        private avio_alloc_context_read_packet readPacketCallback;
        private avio_alloc_context_seek seekCallback;

        private bool inputOpened;
        private bool isDisposed;
        private bool hwDecodingAllowed = true;
        private Stream dataStream;

        private double videoTimeBaseInSeconds;
        private double audioTimeBaseInSeconds;

        // active decoder state
        private volatile float lastDecodedFrameTime;

        private Task decodingTask;
        private CancellationTokenSource decodingTaskCancellationTokenSource;

        private double? skipOutputUntilTime;

        private readonly IRenderer renderer;

        private readonly ConcurrentQueue<DecodedFrame> decodedFrames;
        private readonly ConcurrentQueue<Action> decoderCommands;

        private readonly ConcurrentQueue<Texture> availableTextures;

        private ObjectHandle<VideoDecoder> handle;

        private readonly FFmpegFuncs ffmpeg;

        internal bool Looping;

        static VideoDecoder()
        {
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Linux)
            {
                void loadVersionedLibraryGlobally(string name)
                {
                    int version = FFmpeg.AutoGen.ffmpeg.LibraryVersionMap[name];
                    Library.Load($"lib{name}.so.{version}", Library.LoadFlags.RTLD_LAZY | Library.LoadFlags.RTLD_GLOBAL);
                }

                // FFmpeg.AutoGen doesn't load libraries as RTLD_GLOBAL, so we must load them ourselves to fix inter-library dependencies
                // otherwise they would fallback to the system-installed libraries that can differ in version installed.
                loadVersionedLibraryGlobally("avutil");
                loadVersionedLibraryGlobally("avcodec");
                loadVersionedLibraryGlobally("avformat");
                loadVersionedLibraryGlobally("swscale");
                loadVersionedLibraryGlobally("swresample");
            }
        }

        /// <summary>
        /// Creates a new video decoder that decodes the given video file.
        /// </summary>
        /// <param name="renderer">The renderer to display the video.</param>
        /// <param name="filename">The path to the file that should be decoded.</param>
        public VideoDecoder(IRenderer renderer, string filename)
            : this(renderer, File.OpenRead(filename))
        {
        }

        private VideoDecoder(Stream stream)
        {
            ffmpeg = CreateFuncs();
            dataStream = stream;
            if (!dataStream.CanRead)
                throw new InvalidOperationException($"The given stream does not support reading. A stream used for a {nameof(VideoDecoder)} must support reading.");

            State = DecoderState.Ready;
            decoderCommands = new ConcurrentQueue<Action>();
            handle = new ObjectHandle<VideoDecoder>(this, GCHandleType.Normal);
        }

        /// <summary>
        /// Creates a new video decoder that decodes the given video stream.
        /// </summary>
        /// <param name="renderer">The renderer to display the video.</param>
        /// <param name="videoStream">The stream that should be decoded.</param>
        public VideoDecoder(IRenderer renderer, Stream videoStream)
            : this(videoStream)
        {
            this.renderer = renderer;

            decodedFrames = new ConcurrentQueue<DecodedFrame>();
            availableTextures = new ConcurrentQueue<Texture>(); // TODO: use "real" object pool when there's some public pool supporting disposables
            scalerFrames = new ConcurrentQueue<FFmpegFrame>();
            hwTransferFrames = new ConcurrentQueue<FFmpegFrame>();

            TargetHardwareVideoDecoders.BindValueChanged(_ =>
            {
                // ignore if decoding wasn't initialized yet.
                if (formatContext == null)
                    return;

                decoderCommands.Enqueue(RecreateCodecContext);
            });
        }

        private bool isAudioEnabled;
        private readonly bool audioOnly;

        private int audioRate;
        private int audioChannels;
        private int audioBits;
        private long audioChannelLayout;
        private AVSampleFormat audioFmt;

        public long AudioBitrate => audioCodecContext->bit_rate;
        public long AudioFrameCount => audioStream->nb_frames;

        // Audio mode
        public VideoDecoder(Stream audioStream, int rate, int channels, bool isFloat, int bits, bool signed)
            : this(audioStream)
        {
            audioOnly = true;
            EnableAudioDecoding(rate, channels, isFloat, bits, signed);
        }

        public void EnableAudioDecoding(int rate, int channels, bool isFloat, int bits, bool signed)
        {
            audioRate = rate;
            audioChannels = channels;
            audioBits = bits;

            isAudioEnabled = true;
            audioChannelLayout = ffmpeg.av_get_default_channel_layout(channels);

            memoryStream = new MemoryStream();

            if (isFloat)
                audioFmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;
            else if (!signed && bits == 8)
                audioFmt = AVSampleFormat.AV_SAMPLE_FMT_U8;
            else if (signed && bits == 16)
                audioFmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
            else if (signed && bits == 32)
                audioFmt = AVSampleFormat.AV_SAMPLE_FMT_S32;
            else
                throw new InvalidOperationException("swresample doesn't support provided format!");
        }

        /// <summary>
        /// Seek the decoder to the given timestamp. This will fail if <see cref="CanSeek"/> is false.
        /// </summary>
        /// <param name="targetTimestamp">The timestamp to seek to.</param>
        public void Seek(double targetTimestamp)
        {
            if (!CanSeek)
                throw new InvalidOperationException("This decoder cannot seek because the underlying stream used to decode the video does not support seeking.");

            decoderCommands.Enqueue(() =>
            {
                if (!audioOnly)
                {
                    ffmpeg.avcodec_flush_buffers(videoCodecContext);
                    ffmpeg.av_seek_frame(formatContext, videoStream->index, (long)(targetTimestamp / videoTimeBaseInSeconds / 1000.0), FFmpegFuncs.AVSEEK_FLAG_BACKWARD);
                }

                if (audioStream != null)
                {
                    ffmpeg.avcodec_flush_buffers(audioCodecContext);
                    ffmpeg.av_seek_frame(formatContext, audioStream->index, (long)(targetTimestamp / videoTimeBaseInSeconds / 1000.0), FFmpegFuncs.AVSEEK_FLAG_BACKWARD);
                }

                skipOutputUntilTime = targetTimestamp;
                State = DecoderState.Ready;
            });
        }

        /// <summary>
        /// Returns the given frames back to the decoder, allowing the decoder to reuse the textures contained in the frames to draw new frames.
        /// </summary>
        /// <param name="frames">The frames that should be returned to the decoder.</param>
        public void ReturnFrames(IEnumerable<DecodedFrame> frames)
        {
            foreach (var f in frames)
            {
                f.Texture.FlushUploads();
                availableTextures.Enqueue(f.Texture);
            }
        }

        /// <summary>
        /// Starts the decoding process. The decoding will happen asynchronously in a separate thread. The decoded frames can be retrieved by using <see cref="GetDecodedFrames"/>.
        /// </summary>
        public void StartDecoding()
        {
            if (decodingTask != null)
                throw new InvalidOperationException($"Cannot start decoding once already started. Call {nameof(StopDecodingAsync)} first.");

            // only prepare for decoding if this is our first time starting the decoding process
            if (formatContext == null)
            {
                try
                {
                    PrepareDecoding();
                    RecreateCodecContext();
                }
                catch (Exception e)
                {
                    Logger.Log($"VideoDecoder faulted: {e}");
                    State = DecoderState.Faulted;
                    return;
                }
            }

            decodingTaskCancellationTokenSource = new CancellationTokenSource();
            decodingTask = Task.Factory.StartNew(() => decodingLoop(decodingTaskCancellationTokenSource.Token), decodingTaskCancellationTokenSource.Token, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>
        /// Stops the decoding process.
        /// </summary>
        public Task StopDecodingAsync()
        {
            if (decodingTask == null)
                return Task.CompletedTask;

            decodingTaskCancellationTokenSource.Cancel();

            return decodingTask.ContinueWith(_ =>
            {
                decodingTask = null;
                decodingTaskCancellationTokenSource.Dispose();
                decodingTaskCancellationTokenSource = null;

                State = DecoderState.Ready;
            });
        }

        /// <summary>
        /// Gets all frames that have been decoded by the decoder up until the point in time when this method was called.
        /// Retrieving decoded frames using this method consumes them, ie calling this method again will never retrieve the same frame twice.
        /// </summary>
        /// <returns>The frames that have been decoded up until the point in time this method was called.</returns>
        public IEnumerable<DecodedFrame> GetDecodedFrames()
        {
            var frames = new List<DecodedFrame>(decodedFrames.Count);
            while (decodedFrames.TryDequeue(out var df))
                frames.Add(df);

            return frames;
        }

        // https://en.wikipedia.org/wiki/YCbCr
        public Matrix3 GetConversionMatrix()
        {
            if (videoCodecContext == null)
                return Matrix3.Zero;

            switch (videoCodecContext->colorspace)
            {
                case AVColorSpace.AVCOL_SPC_BT709:
                    return new Matrix3(1.164f, 1.164f, 1.164f,
                        0.000f, -0.213f, 2.112f,
                        1.793f, -0.533f, 0.000f);

                case AVColorSpace.AVCOL_SPC_UNSPECIFIED:
                case AVColorSpace.AVCOL_SPC_SMPTE170M:
                case AVColorSpace.AVCOL_SPC_SMPTE240M:
                default:
                    return new Matrix3(1.164f, 1.164f, 1.164f,
                        0.000f, -0.392f, 2.017f,
                        1.596f, -0.813f, 0.000f);
            }
        }

        [MonoPInvokeCallback(typeof(avio_alloc_context_read_packet))]
        private static int readPacket(void* opaque, byte* bufferPtr, int bufferSize)
        {
            var handle = new ObjectHandle<VideoDecoder>((IntPtr)opaque);
            if (!handle.GetTarget(out VideoDecoder decoder))
                return 0;

            var span = new Span<byte>(bufferPtr, bufferSize);
            int bytesRead = decoder.dataStream.Read(span);

            return bytesRead != 0 ? bytesRead : FFmpegFuncs.AVERROR_EOF;
        }

        [MonoPInvokeCallback(typeof(avio_alloc_context_seek))]
        private static long streamSeekCallbacks(void* opaque, long offset, int whence)
        {
            var handle = new ObjectHandle<VideoDecoder>((IntPtr)opaque);
            if (!handle.GetTarget(out VideoDecoder decoder))
                return -1;

            if (!decoder.dataStream.CanSeek)
                throw new InvalidOperationException("Tried seeking on a video sourced by a non-seekable stream.");

            switch (whence)
            {
                case StdIo.SEEK_CUR:
                    decoder.dataStream.Seek(offset, SeekOrigin.Current);
                    break;

                case StdIo.SEEK_END:
                    decoder.dataStream.Seek(offset, SeekOrigin.End);
                    break;

                case StdIo.SEEK_SET:
                    decoder.dataStream.Seek(offset, SeekOrigin.Begin);
                    break;

                case FFmpegFuncs.AVSEEK_SIZE:
                    return decoder.dataStream.Length;

                default:
                    return -1;
            }

            return decoder.dataStream.Position;
        }

        // sets up libavformat state: creates the AVFormatContext, the frames, etc. to start decoding, but does not actually start the decodingLoop
        internal void PrepareDecoding()
        {
            dataStream.Position = 0;

            const int context_buffer_size = 4096;
            readPacketCallback = readPacket;
            seekCallback = streamSeekCallbacks;
            // we shouldn't keep a reference to this buffer as it can be freed and replaced by the native libs themselves.
            // https://ffmpeg.org/doxygen/4.1/aviobuf_8c.html#a853f5149136a27ffba3207d8520172a5
            byte* contextBuffer = (byte*)ffmpeg.av_malloc(context_buffer_size);

            ioContext = ffmpeg.avio_alloc_context(contextBuffer, context_buffer_size, 0, (void*)handle.Handle, readPacketCallback, null, seekCallback);

            var fcPtr = ffmpeg.avformat_alloc_context();
            formatContext = fcPtr;
            formatContext->pb = ioContext;
            formatContext->flags |= FFmpegFuncs.AVFMT_FLAG_GENPTS; // required for most HW decoders as they only read `pts`

            AVDictionary* options = null;
            // see https://github.com/ppy/osu/issues/13696 for reasoning
            ffmpeg.av_dict_set?.Invoke(&options, "ignore_editlist", "1", 0);
            int openInputResult = ffmpeg.avformat_open_input(&fcPtr, "pipe:", null, &options);
            ffmpeg.av_dict_free?.Invoke(&options);

            inputOpened = openInputResult >= 0;
            if (!inputOpened)
                throw new InvalidOperationException($"Error opening file or stream: {getErrorMessage(openInputResult)}");

            int findStreamInfoResult = ffmpeg.avformat_find_stream_info(formatContext, null);
            if (findStreamInfoResult < 0)
                throw new InvalidOperationException($"Error finding stream info: {getErrorMessage(findStreamInfoResult)}");

            int streamIndex = -1;

            if (!audioOnly)
            {
                streamIndex = ffmpeg.av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (streamIndex < 0)
                    throw new InvalidOperationException($"Couldn't find stream: {getErrorMessage(streamIndex)}");

                videoStream = formatContext->streams[streamIndex];
                videoTimeBaseInSeconds = videoStream->time_base.GetValue();

                if (videoStream->duration > 0)
                    Duration = videoStream->duration * videoTimeBaseInSeconds * 1000.0;
                else
                    Duration = formatContext->duration / (double)FFmpegFuncs.AV_TIME_BASE * 1000.0;
            }

            if (isAudioEnabled)
            {
                streamIndex = ffmpeg.av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, streamIndex, null, 0);
                if (streamIndex < 0 && audioOnly)
                    throw new InvalidOperationException($"Couldn't find stream: {getErrorMessage(streamIndex)}");

                audioStream = formatContext->streams[streamIndex];
                audioTimeBaseInSeconds = audioStream->time_base.GetValue();

                if (audioOnly)
                {
                    if (audioStream->duration > 0)
                        Duration = audioStream->duration * audioTimeBaseInSeconds * 1000.0;
                    else
                        Duration = formatContext->duration / (double)FFmpegFuncs.AV_TIME_BASE * 1000.0;
                }
            }

            packet = ffmpeg.av_packet_alloc();
            receiveFrame = ffmpeg.av_frame_alloc();
        }

        internal void OpenAudioStream()
        {
            if (audioStream == null)
                return;

            int result = ffmpeg.avcodec_open2(audioStream->codec, ffmpeg.avcodec_find_decoder(audioStream->codec->codec_id), null);

            if (result < 0)
                throw new InvalidDataException($"Error trying to open audio codec: {getErrorMessage(result)}");

            if (!prepareResampler())
                throw new InvalidDataException("Error trying to prepare audio resampler");
        }

        internal void RecreateCodecContext()
        {
            if (videoStream == null)
                return;

            var codecParams = *videoStream->codecpar;
            var targetHwDecoders = hwDecodingAllowed ? TargetHardwareVideoDecoders.Value : HardwareVideoDecoder.None;
            bool openSuccessful = false;

            foreach (var (decoder, hwDeviceType) in GetAvailableDecoders(formatContext->iformat, codecParams.codec_id, targetHwDecoders))
            {
                // free context in case it was allocated in a previous iteration or recreate call.
                if (videoCodecContext != null)
                {
                    fixed (AVCodecContext** ptr = &videoCodecContext)
                        ffmpeg.avcodec_free_context(ptr);
                }

                videoCodecContext = ffmpeg.avcodec_alloc_context3(decoder.Pointer);
                videoCodecContext->pkt_timebase = videoStream->time_base;

                if (videoCodecContext == null)
                {
                    Logger.Log($"Couldn't allocate codec context. Codec: {decoder.Name}");
                    continue;
                }

                int paramCopyResult = ffmpeg.avcodec_parameters_to_context(videoCodecContext, &codecParams);

                if (paramCopyResult < 0)
                {
                    Logger.Log($"Couldn't copy codec parameters from {decoder.Name}: {getErrorMessage(paramCopyResult)}");
                    continue;
                }

                // initialize hardware decode context.
                if (hwDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    int hwDeviceCreateResult = ffmpeg.av_hwdevice_ctx_create(&videoCodecContext->hw_device_ctx, hwDeviceType, null, null, 0);

                    if (hwDeviceCreateResult < 0)
                    {
                        Logger.Log($"Couldn't create hardware video decoder context {hwDeviceType} for codec {decoder.Name}: {getErrorMessage(hwDeviceCreateResult)}");
                        continue;
                    }

                    Logger.Log($"Successfully opened hardware video decoder context {hwDeviceType} for codec {decoder.Name}");
                }

                int openCodecResult = ffmpeg.avcodec_open2(videoCodecContext, decoder.Pointer, null);

                if (openCodecResult < 0)
                {
                    Logger.Log($"Error trying to open {decoder.Name} codec: {getErrorMessage(openCodecResult)}");
                    continue;
                }

                Logger.Log($"Successfully initialized decoder: {decoder.Name}");

                openSuccessful = true;
                break;
            }

            if (!openSuccessful)
                throw new InvalidOperationException($"No usable decoder found for codec ID {codecParams.codec_id}");

            OpenAudioStream();
        }

        private bool prepareResampler()
        {
            long srcChLayout = ffmpeg.av_get_default_channel_layout(audioCodecContext->channels);
            AVSampleFormat srcAudioFmt = audioCodecContext->sample_fmt;
            int srcRate = audioCodecContext->sample_rate;

            if (audioChannelLayout == srcChLayout && audioFmt == srcAudioFmt && audioRate == srcRate)
            {
                swrContext = null;
                return true;
            }

            swrContext = ffmpeg.swr_alloc_set_opts(null, audioChannelLayout, audioFmt, audioRate,
                srcChLayout, srcAudioFmt, srcRate, 0, null);

            if (swrContext == null)
            {
                Logger.Log("Failed allocating memory for swresampler", level: LogLevel.Error);
                return false;
            }

            ffmpeg.swr_init(swrContext);

            return ffmpeg.swr_is_initialized(swrContext) > 0;
        }

        private AVPacket* packet;
        private AVFrame* receiveFrame;

        private void decodingLoop(CancellationToken cancellationToken)
        {
            const int max_pending_frames = 3;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    switch (State)
                    {
                        case DecoderState.Ready:
                        case DecoderState.Running:
                            if (decodedFrames.Count < max_pending_frames)
                            {
                                decodeNextFrame(packet, receiveFrame);
                            }
                            else
                            {
                                // wait until existing buffers are consumed.
                                State = DecoderState.Ready;
                                Thread.Sleep(1);
                            }

                            break;

                        case DecoderState.EndOfStream:
                            // While at the end of the stream, avoid attempting to read further as this comes with a non-negligible overhead.
                            // A Seek() operation will trigger a state change, allowing decoding to potentially start again.
                            Thread.Sleep(50);
                            break;

                        default:
                            Debug.Fail($"Video decoder should never be in a \"{State}\" state during decode.");
                            return;
                    }

                    while (!decoderCommands.IsEmpty)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        if (decoderCommands.TryDequeue(out var cmd))
                            cmd();
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "VideoDecoder faulted");
                State = DecoderState.Faulted;
            }
            finally
            {
                if (State != DecoderState.Faulted)
                    State = DecoderState.Stopped;
            }
        }

        private MemoryStream memoryStream;

        internal int DecodeNextAudioFrame(int iteration, out byte[] decodedAudio, bool decodeUntilEnd = false)
        {
            if (audioStream == null)
            {
                decodedAudio = Array.Empty<byte>();
                return 0;
            }

            memoryStream.Position = 0;

            try
            {
                int i = 0;

                while (decodeUntilEnd || i++ < iteration)
                {
                    decodeNextFrame(packet, receiveFrame);

                    if (State != DecoderState.Running)
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "VideoDecoder faulted while decoding audio");
                State = DecoderState.Faulted;
                decodedAudio = Array.Empty<byte>();
                return 0;
            }

            decodedAudio = memoryStream.GetBuffer();

            return (int)memoryStream.Position;
        }

        private void decodeNextFrame(AVPacket* packet, AVFrame* receiveFrame)
        {
            // read data from input into AVPacket.
            // only read if the packet is empty, otherwise we would overwrite what's already there which can lead to visual glitches.
            int readFrameResult = 0;
            if (packet->buf == null)
                readFrameResult = ffmpeg.av_read_frame(formatContext, packet);

            if (readFrameResult >= 0)
            {
                State = DecoderState.Running;

                bool unrefPacket = true;

                AVCodecContext* codecContext =
                    !audioOnly && packet->stream_index == videoStream->index ? videoCodecContext
                    : audioStream != null && packet->stream_index == audioStream->index ? audioCodecContext : null;

                if (codecContext != null)
                {
                    int sendPacketResult = sendPacket(codecContext, receiveFrame, packet);

                    // keep the packet data for next frame if we didn't send it successfully.
                    if (sendPacketResult == -FFmpegFuncs.EAGAIN)
                    {
                        unrefPacket = false;
                    }
                }

                if (unrefPacket)
                    ffmpeg.av_packet_unref(packet);
            }
            else if (readFrameResult == FFmpegFuncs.AVERROR_EOF)
            {
                // Flush decoder.
                if (!audioOnly)
                    sendPacket(videoCodecContext, receiveFrame, null);

                if (audioStream != null)
                {
                    sendPacket(audioCodecContext, receiveFrame, null);
                    resampleAndAppendToAudioStream(null); // flush audio resampler
                }

                if (Looping)
                {
                    Seek(0);
                }
                else
                {
                    // This marks the video stream as no longer relevant (until a future potential Seek operation).
                    State = DecoderState.EndOfStream;
                }
            }
            else if (readFrameResult == -FFmpegFuncs.EAGAIN)
            {
                State = DecoderState.Ready;
                Thread.Sleep(1);
            }
            else
            {
                Logger.Log($"Failed to read data into avcodec packet: {getErrorMessage(readFrameResult)}");
                Thread.Sleep(1);
            }
        }

        private int sendPacket(AVCodecContext* codecContext, AVFrame* receiveFrame, AVPacket* packet)
        {
            // send the packet for decoding.
            int sendPacketResult = ffmpeg.avcodec_send_packet(codecContext, packet);

            // Note: EAGAIN can be returned if there's too many pending frames, which we have to read,
            // otherwise we would get stuck in an infinite loop.
            if (sendPacketResult == 0 || sendPacketResult == -FFmpegFuncs.EAGAIN)
            {
                readDecodedFrames(codecContext, receiveFrame);
            }
            else
            {
                Logger.Log($"Failed to send avcodec packet: {getErrorMessage(sendPacketResult)}");
                tryDisableHwDecoding(sendPacketResult);
            }

            return sendPacketResult;
        }

        private readonly ConcurrentQueue<FFmpegFrame> hwTransferFrames;
        private void returnHwTransferFrame(FFmpegFrame frame) => hwTransferFrames.Enqueue(frame);

        private void readDecodedFrames(AVCodecContext* codecContext, AVFrame* receiveFrame)
        {
            while (true)
            {
                int receiveFrameResult = ffmpeg.avcodec_receive_frame(codecContext, receiveFrame);

                if (receiveFrameResult < 0)
                {
                    if (receiveFrameResult != -FFmpegFuncs.EAGAIN && receiveFrameResult != FFmpegFuncs.AVERROR_EOF)
                    {
                        Logger.Log($"Failed to receive frame from avcodec: {getErrorMessage(receiveFrameResult)}");
                        tryDisableHwDecoding(receiveFrameResult);
                    }

                    break;
                }

                // use `best_effort_timestamp` as it can be more accurate if timestamps from the source file (pts) are broken.
                // but some HW codecs don't set it in which case fallback to `pts`
                long frameTimestamp = receiveFrame->best_effort_timestamp != FFmpegFuncs.AV_NOPTS_VALUE ? receiveFrame->best_effort_timestamp : receiveFrame->pts;

                double frameTime = 0.0;

                if (audioStream != null && codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    frameTime = (frameTimestamp - audioStream->start_time) * audioTimeBaseInSeconds * 1000;

                    if (skipOutputUntilTime > frameTime)
                        continue;

                    resampleAndAppendToAudioStream(receiveFrame);
                }
                else if (!audioOnly && codecContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    frameTime = (frameTimestamp - videoStream->start_time) * videoTimeBaseInSeconds * 1000;

                    if (skipOutputUntilTime > frameTime)
                        continue;

                    // get final frame.
                    FFmpegFrame frame;

                    if (((AVPixelFormat)receiveFrame->format).IsHardwarePixelFormat())
                    {
                        // transfer data from HW decoder to RAM.
                        if (!hwTransferFrames.TryDequeue(out var hwTransferFrame))
                            hwTransferFrame = new FFmpegFrame(ffmpeg, returnHwTransferFrame);

                        // WARNING: frames from `av_hwframe_transfer_data` have their timestamps set to AV_NOPTS_VALUE instead of real values.
                        // if you need to use them later, take them from `receiveFrame`.
                        int transferResult = ffmpeg.av_hwframe_transfer_data(hwTransferFrame.Pointer, receiveFrame, 0);

                        if (transferResult < 0)
                        {
                            Logger.Log($"Failed to transfer frame from HW decoder: {getErrorMessage(transferResult)}");
                            tryDisableHwDecoding(transferResult);

                            hwTransferFrame.Dispose();
                            continue;
                        }

                        frame = hwTransferFrame;
                    }
                    else
                    {
                        // copy data to a new AVFrame so that `receiveFrame` can be reused.
                        frame = new FFmpegFrame(ffmpeg);
                        ffmpeg.av_frame_move_ref(frame.Pointer, receiveFrame);
                    }

                    // Note: this is the pixel format that `VideoTexture` expects internally
                    frame = ensureFramePixelFormat(frame, AVPixelFormat.AV_PIX_FMT_YUV420P);
                    if (frame == null)
                        continue;

                    if (!availableTextures.TryDequeue(out var tex))
                        tex = renderer.CreateVideoTexture(frame.Pointer->width, frame.Pointer->height);

                    var upload = new VideoTextureUpload(frame);

                    // We do not support videos with transparency at this point, so the upload's opacity as well as the texture's opacity is always opaque.
                    tex.SetData(upload, Opacity.Opaque);
                    decodedFrames.Enqueue(new DecodedFrame { Time = frameTime, Texture = tex });
                }

                lastDecodedFrameTime = (float)frameTime;
            }
        }

        private void resampleAndAppendToAudioStream(AVFrame* frame)
        {
            if (memoryStream == null || audioStream == null)
                return;

            int sampleCount;
            byte*[] source;

            if (swrContext != null)
            {
                sampleCount = (int)ffmpeg.swr_get_delay(swrContext, audioCodecContext->sample_rate);
                source = null;

                if (frame != null)
                {
                    sampleCount = (int)Math.Ceiling((double)(sampleCount + frame->nb_samples) * audioRate / audioCodecContext->sample_rate);
                    source = frame->data.ToArray();
                }

                // no frame, no remaining samples in resampler
                if (sampleCount <= 0)
                    return;
            }
            else if (frame != null)
            {
                sampleCount = frame->nb_samples;
                source = frame->data.ToArray();
            }
            else // no frame, no resampler
            {
                return;
            }

            int audioSize = ffmpeg.av_samples_get_buffer_size(null, audioChannels, sampleCount, audioFmt, 0);
            byte[] audioDest = ArrayPool<byte>.Shared.Rent(audioSize);
            int nbSamples = 0;

            try
            {
                if (swrContext != null)
                {
                    fixed (byte** data = source)
                    fixed (byte* dest = audioDest)
                        nbSamples = ffmpeg.swr_convert(swrContext, &dest, sampleCount, data, frame != null ? frame->nb_samples : 0);
                }
                else if (source != null)
                {
                    // assuming that the destination and source are not planar as we never define planar in ctor
                    nbSamples = sampleCount;

                    for (int i = 0; i < audioSize; i++)
                    {
                        audioDest[i] = *(source[0] + i);
                    }
                }

                if (nbSamples > 0)
                    memoryStream.Write(audioDest, 0, nbSamples * (audioBits / 8) * audioChannels);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(audioDest);
            }
        }

        private readonly ConcurrentQueue<FFmpegFrame> scalerFrames;
        private void returnScalerFrame(FFmpegFrame frame) => scalerFrames.Enqueue(frame);

        [CanBeNull]
        private FFmpegFrame ensureFramePixelFormat(FFmpegFrame frame, AVPixelFormat targetPixelFormat)
        {
            if (frame.PixelFormat == targetPixelFormat)
                return frame;

            int width = frame.Pointer->width;
            int height = frame.Pointer->height;

            swsContext = ffmpeg.sws_getCachedContext(
                swsContext,
                width, height, frame.PixelFormat,
                width, height, targetPixelFormat,
                1, null, null, null);

            if (!scalerFrames.TryDequeue(out var scalerFrame))
                scalerFrame = new FFmpegFrame(ffmpeg, returnScalerFrame);

            // (re)initialize the scaler frame if needed.
            if (scalerFrame.PixelFormat != targetPixelFormat || scalerFrame.Pointer->width != width || scalerFrame.Pointer->height != height)
            {
                ffmpeg.av_frame_unref(scalerFrame.Pointer);

                // Note: this field determines the scaler's output pix format.
                scalerFrame.PixelFormat = targetPixelFormat;
                scalerFrame.Pointer->width = width;
                scalerFrame.Pointer->height = height;

                int getBufferResult = ffmpeg.av_frame_get_buffer(scalerFrame.Pointer, 0);

                if (getBufferResult < 0)
                {
                    Logger.Log($"Failed to allocate SWS frame buffer: {getErrorMessage(getBufferResult)}");

                    scalerFrame.Dispose();
                    frame.Return();
                    return null;
                }
            }

            int scalerResult = ffmpeg.sws_scale(
                swsContext,
                frame.Pointer->data, frame.Pointer->linesize, 0, height,
                scalerFrame.Pointer->data, scalerFrame.Pointer->linesize);

            // return the original frame regardless of the scaler result.
            frame.Return();

            if (scalerResult < 0)
            {
                Logger.Log($"Failed to scale frame: {getErrorMessage(scalerResult)}");

                scalerFrame.Dispose();
                return null;
            }

            return scalerFrame;
        }

        private void tryDisableHwDecoding(int errorCode)
        {
            if (!hwDecodingAllowed || TargetHardwareVideoDecoders.Value == HardwareVideoDecoder.None || videoCodecContext == null || videoCodecContext->hw_device_ctx == null)
                return;

            hwDecodingAllowed = false;

            if (errorCode == -FFmpegFuncs.ENOMEM)
            {
                Logger.Log("Disabling hardware decoding of all videos due to a lack of memory");
                TargetHardwareVideoDecoders.Value = HardwareVideoDecoder.None;

                // `recreateCodecContext` will be called by the bindable hook
            }
            else
            {
                Logger.Log("Disabling hardware decoding of the current video due to an unexpected error");

                decoderCommands.Enqueue(RecreateCodecContext);
            }
        }

        private string getErrorMessage(int errorCode)
        {
            const ulong buffer_size = 256;
            byte[] buffer = new byte[buffer_size];

            int strErrorCode;

            fixed (byte* bufPtr = buffer)
            {
                strErrorCode = ffmpeg.av_strerror(errorCode, bufPtr, buffer_size);
            }

            if (strErrorCode < 0)
                return $"{errorCode} (av_strerror failed with code {strErrorCode})";

            int messageLength = Math.Max(0, Array.IndexOf(buffer, (byte)0));
            return $"{Encoding.ASCII.GetString(buffer[..messageLength])} ({errorCode})";
        }

        /// <remarks>
        /// Returned HW devices are not guaranteed to be available on the current machine, they only represent what the loaded FFmpeg libraries support.
        /// </remarks>
        protected virtual IEnumerable<(FFmpegCodec codec, AVHWDeviceType hwDeviceType)> GetAvailableDecoders(
            AVInputFormat* inputFormat,
            AVCodecID codecId,
            HardwareVideoDecoder targetHwDecoders
        )
        {
            var comparer = new AVHWDeviceTypePerformanceComparer();
            var codecs = new Lists.SortedList<(FFmpegCodec, AVHWDeviceType hwDeviceType)>((x, y) => comparer.Compare(x.hwDeviceType, y.hwDeviceType));
            FFmpegCodec firstCodec = null;

            void* iterator = null;

            while (true)
            {
                var avCodec = ffmpeg.av_codec_iterate(&iterator);

                if (avCodec == null) break;

                var codec = new FFmpegCodec(ffmpeg, avCodec);
                if (codec.Id != codecId || !codec.IsDecoder) continue;

                firstCodec ??= codec;

                if (targetHwDecoders == HardwareVideoDecoder.None)
                    break;

                foreach (var hwDeviceType in codec.SupportedHwDeviceTypes.Value)
                {
                    var hwVideoDecoder = hwDeviceType.ToHardwareVideoDecoder();

                    if (!hwVideoDecoder.HasValue || !targetHwDecoders.HasFlagFast(hwVideoDecoder.Value))
                        continue;

                    codecs.Add((codec, hwDeviceType));
                }
            }

            // default to the first codec that we found with no HW devices.
            // The first codec is what FFmpeg's `avcodec_find_decoder` would return so this way we'll automatically fallback to that.
            if (firstCodec != null)
                codecs.Add((firstCodec, AVHWDeviceType.AV_HWDEVICE_TYPE_NONE));

            return codecs;
        }

        protected virtual FFmpegFuncs CreateFuncs()
        {
            // other frameworks should handle native libraries themselves
            FFmpeg.AutoGen.ffmpeg.GetOrLoadLibrary = name =>
            {
                int version = FFmpeg.AutoGen.ffmpeg.LibraryVersionMap[name];

                // "lib" prefix and extensions are resolved by .net core
                string libraryName;

                switch (RuntimeInfo.OS)
                {
                    case RuntimeInfo.Platform.macOS:
                        libraryName = $"{name}.{version}";
                        break;

                    case RuntimeInfo.Platform.Windows:
                        libraryName = $"{name}-{version}";
                        break;

                    // To handle versioning in Linux, we have to specify the entire file name
                    // because Linux uses a version suffix after the file extension (e.g. libavutil.so.56)
                    // More info: https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading?view=net-6.0
                    case RuntimeInfo.Platform.Linux:
                        libraryName = $"lib{name}.so.{version}";
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(RuntimeInfo.OS), RuntimeInfo.OS, null);
                }

                return NativeLibrary.Load(libraryName, RuntimeInfo.EntryAssembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            };

            return new FFmpegFuncs
            {
                av_dict_set = FFmpeg.AutoGen.ffmpeg.av_dict_set,
                av_dict_free = FFmpeg.AutoGen.ffmpeg.av_dict_free,
                av_frame_alloc = FFmpeg.AutoGen.ffmpeg.av_frame_alloc,
                av_frame_free = FFmpeg.AutoGen.ffmpeg.av_frame_free,
                av_frame_unref = FFmpeg.AutoGen.ffmpeg.av_frame_unref,
                av_frame_move_ref = FFmpeg.AutoGen.ffmpeg.av_frame_move_ref,
                av_frame_get_buffer = FFmpeg.AutoGen.ffmpeg.av_frame_get_buffer,
                av_strdup = FFmpeg.AutoGen.ffmpeg.av_strdup,
                av_strerror = FFmpeg.AutoGen.ffmpeg.av_strerror,
                av_malloc = FFmpeg.AutoGen.ffmpeg.av_malloc,
                av_freep = FFmpeg.AutoGen.ffmpeg.av_freep,
                av_packet_alloc = FFmpeg.AutoGen.ffmpeg.av_packet_alloc,
                av_packet_unref = FFmpeg.AutoGen.ffmpeg.av_packet_unref,
                av_packet_free = FFmpeg.AutoGen.ffmpeg.av_packet_free,
                av_read_frame = FFmpeg.AutoGen.ffmpeg.av_read_frame,
                av_seek_frame = FFmpeg.AutoGen.ffmpeg.av_seek_frame,
                av_hwdevice_ctx_create = FFmpeg.AutoGen.ffmpeg.av_hwdevice_ctx_create,
                av_hwframe_transfer_data = FFmpeg.AutoGen.ffmpeg.av_hwframe_transfer_data,
                av_codec_iterate = FFmpeg.AutoGen.ffmpeg.av_codec_iterate,
                av_codec_is_decoder = FFmpeg.AutoGen.ffmpeg.av_codec_is_decoder,
                avcodec_get_hw_config = FFmpeg.AutoGen.ffmpeg.avcodec_get_hw_config,
                avcodec_alloc_context3 = FFmpeg.AutoGen.ffmpeg.avcodec_alloc_context3,
                avcodec_free_context = FFmpeg.AutoGen.ffmpeg.avcodec_free_context,
                avcodec_parameters_to_context = FFmpeg.AutoGen.ffmpeg.avcodec_parameters_to_context,
                avcodec_open2 = FFmpeg.AutoGen.ffmpeg.avcodec_open2,
                avcodec_receive_frame = FFmpeg.AutoGen.ffmpeg.avcodec_receive_frame,
                avcodec_send_packet = FFmpeg.AutoGen.ffmpeg.avcodec_send_packet,
                avcodec_flush_buffers = FFmpeg.AutoGen.ffmpeg.avcodec_flush_buffers,
                avformat_alloc_context = FFmpeg.AutoGen.ffmpeg.avformat_alloc_context,
                avformat_close_input = FFmpeg.AutoGen.ffmpeg.avformat_close_input,
                avformat_find_stream_info = FFmpeg.AutoGen.ffmpeg.avformat_find_stream_info,
                avformat_open_input = FFmpeg.AutoGen.ffmpeg.avformat_open_input,
                av_find_best_stream = FFmpeg.AutoGen.ffmpeg.av_find_best_stream,
                avio_alloc_context = FFmpeg.AutoGen.ffmpeg.avio_alloc_context,
                avio_context_free = FFmpeg.AutoGen.ffmpeg.avio_context_free,
                sws_freeContext = FFmpeg.AutoGen.ffmpeg.sws_freeContext,
                sws_getCachedContext = FFmpeg.AutoGen.ffmpeg.sws_getCachedContext,
                sws_scale = FFmpeg.AutoGen.ffmpeg.sws_scale,
                swr_alloc_set_opts = FFmpeg.AutoGen.ffmpeg.swr_alloc_set_opts,
                swr_init = FFmpeg.AutoGen.ffmpeg.swr_init,
                swr_is_initialized = FFmpeg.AutoGen.ffmpeg.swr_is_initialized,
                swr_free = FFmpeg.AutoGen.ffmpeg.swr_free,
                swr_close = FFmpeg.AutoGen.ffmpeg.swr_close,
                swr_convert = FFmpeg.AutoGen.ffmpeg.swr_convert,
                swr_get_delay = FFmpeg.AutoGen.ffmpeg.swr_get_delay,
                av_samples_get_buffer_size = FFmpeg.AutoGen.ffmpeg.av_samples_get_buffer_size,
                av_get_default_channel_layout = FFmpeg.AutoGen.ffmpeg.av_get_default_channel_layout,
                avcodec_find_decoder = FFmpeg.AutoGen.ffmpeg.avcodec_find_decoder
            };
        }

        #region Disposal

        ~VideoDecoder()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed)
                return;

            isDisposed = true;

            decoderCommands.Clear();

            void freeFFmpeg()
            {
                if (packet != null)
                {
                    fixed (AVPacket** ptr = &packet)
                        ffmpeg.av_packet_free(ptr);
                }

                if (receiveFrame != null)
                {
                    fixed (AVFrame** ptr = &receiveFrame)
                        ffmpeg.av_frame_free(ptr);
                }

                if (formatContext != null && inputOpened)
                {
                    fixed (AVFormatContext** ptr = &formatContext)
                        ffmpeg.avformat_close_input(ptr);
                }

                if (ioContext != null)
                {
                    // This is not handled by avformat_close_input for custom IO:
                    // https://ffmpeg.org/doxygen/4.3/structAVFormatContext.html#a1e7324262b6b78522e52064daaa7bc87
                    ffmpeg.av_freep(&ioContext->buffer);

                    fixed (AVIOContext** ptr = &ioContext)
                        ffmpeg.avio_context_free(ptr);
                }

                if (videoCodecContext != null)
                {
                    fixed (AVCodecContext** ptr = &videoCodecContext)
                        ffmpeg.avcodec_free_context(ptr);
                }

                seekCallback = null;
                readPacketCallback = null;

                if (!audioOnly)
                    dataStream.Dispose();

                dataStream = null;

                if (swsContext != null)
                    ffmpeg.sws_freeContext(swsContext);

                if (swrContext != null)
                {
                    fixed (SwrContext** ptr = &swrContext)
                        ffmpeg.swr_free(ptr);
                }

                memoryStream?.Dispose();

                memoryStream = null;

                if (!audioOnly)
                {
                    while (decodedFrames.TryDequeue(out var f))
                    {
                        f.Texture.FlushUploads();
                        f.Texture.Dispose();
                    }

                    while (availableTextures.TryDequeue(out var t))
                        t.Dispose();

                    while (hwTransferFrames.TryDequeue(out var hwF))
                        hwF.Dispose();

                    while (scalerFrames.TryDequeue(out var sf))
                        sf.Dispose();
                }

                handle.Dispose();
            }

            if (audioOnly)
                freeFFmpeg();
            else
                StopDecodingAsync().ContinueWith(_ => freeFFmpeg());
        }

        #endregion

        /// <summary>
        /// Represents the possible states the decoder can be in.
        /// </summary>
        public enum DecoderState
        {
            /// <summary>
            /// The decoder is ready to begin decoding. This is the default state before the decoder starts operations.
            /// </summary>
            Ready = 0,

            /// <summary>
            /// The decoder is currently running and decoding frames.
            /// </summary>
            Running = 1,

            /// <summary>
            /// The decoder has faulted with an exception.
            /// </summary>
            Faulted = 2,

            /// <summary>
            /// The decoder has reached the end of the video data.
            /// </summary>
            EndOfStream = 3,

            /// <summary>
            /// The decoder has been completely stopped and cannot be resumed.
            /// </summary>
            Stopped = 4,
        }
    }
}
