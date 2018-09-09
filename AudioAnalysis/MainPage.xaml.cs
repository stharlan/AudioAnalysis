using Microsoft.Graphics.Canvas.Brushes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Devices;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace AudioAnalysis
{

    [ComImport]
    [Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {

        private AudioGraph m_AudioGraph = null;
        private AudioDeviceInputNode m_AudioDevideInputNode = null;
        private AudioFrameOutputNode m_AudioFrameOutputNode = null;
        private DeviceInformationCollection m_DevInfoColl = null;
        private List<DeviceName> m_DeviceNames = new List<DeviceName>();
        private DeviceInformation m_SelectedDevice = null;
        private uint m_Capacity = 0;
        private uint m_abCap = 0;
        private uint m_abLen = 0;
        private float[] m_QuantumSamples;

        private int m_FFTSampleSize = 0;
        private IntPtr pin = IntPtr.Zero;
        private IntPtr pout = IntPtr.Zero;
        private IntPtr fplan = IntPtr.Zero;

        public MainPage()
        {
            this.InitializeComponent();            
        }

        private void ShowMessage(string message)
        {
            IAsyncAction aa = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                MessageDialog msgdlg = new MessageDialog(message);
                msgdlg.Commands.Add(new UICommand("Ok") { Id = 0 });
                await msgdlg.ShowAsync();
            });
        }

        protected override void OnNavigatedTo(NavigationEventArgs args)
        {

            IAsyncOperation<DeviceInformationCollection> resultb =
                DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
            resultb.Completed = new AsyncOperationCompletedHandler<DeviceInformationCollection>(OnFindAllCaptureDevices);

            base.OnNavigatedTo(args);
        }

        private void OnFindAllCaptureDevices(IAsyncOperation<DeviceInformationCollection> asyncInfo, AsyncStatus asyncStatus)
        {
            if(asyncStatus == AsyncStatus.Completed)
            {
                m_DevInfoColl = asyncInfo.GetResults();

                // fill the device name array
                this.m_DeviceNames = new List<DeviceName>();
                foreach (DeviceInformation devInfo in this.m_DevInfoColl)
                {
                    this.m_DeviceNames.Add(new DeviceName() { DevName = devInfo.Name, DevId = devInfo.Id });
                }

                // create the audio graph
                AudioGraphSettings audioGraphSettings = new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Other);
                if(this.m_DevInfoColl.Count() > 0)
                {
                    this.m_SelectedDevice = this.m_DevInfoColl[0];
                } else
                {
                    this.m_SelectedDevice = null;
                }
                IAsyncOperation<CreateAudioGraphResult> resulta = AudioGraph.CreateAsync(audioGraphSettings);
                resulta.Completed = new AsyncOperationCompletedHandler<CreateAudioGraphResult>(OnCreateGraphCompleted);

                // fill the combo box
                IAsyncAction aa = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    cmbDevices.ItemsSource = this.m_DeviceNames;
                    cmbDevices.DisplayMemberPath = "DevName";
                    cmbDevices.SelectedValuePath = "DevId";
                    if (this.m_DeviceNames.Count() > 0) cmbDevices.SelectedIndex = 0;
                });
            }
        }

        unsafe private void OnCreateGraphCompleted(IAsyncOperation<CreateAudioGraphResult> asyncInfo, AsyncStatus asyncStatus)
        {
            if(asyncStatus == AsyncStatus.Completed)
            {
                CreateAudioGraphResult result = asyncInfo.GetResults();
                if (result.Status == AudioGraphCreationStatus.Success)
                {
                    this.m_AudioGraph = result.Graph;

                    this.m_QuantumSamples = new float[this.m_AudioGraph.SamplesPerQuantum * this.m_AudioGraph.EncodingProperties.ChannelCount];

                    m_FFTSampleSize = 2;
                    while(m_FFTSampleSize < this.m_AudioGraph.SamplesPerQuantum)
                    {
                        m_FFTSampleSize *= 2;
                        if(pin != IntPtr.Zero)
                        {
                            FFTWSharp.fftwf.free(pin);
                            pin = IntPtr.Zero;
                        }
                        if(pout != IntPtr.Zero)
                        {
                            FFTWSharp.fftwf.free(pout);
                            pout = IntPtr.Zero;
                        }
                        pin = FFTWSharp.fftwf.malloc(m_FFTSampleSize * 8);
                        pout = FFTWSharp.fftwf.malloc(m_FFTSampleSize * 8);
                        float* fpin = (float*)pin;
                        float* fpout = (float*)pout;
                        for (int i=0; i<m_FFTSampleSize*2; i++)
                        {
                            fpin[i] = 0.0f;
                            fpout[i] = 0.0f;
                        }
                        if(fplan != IntPtr.Zero)
                        {
                            FFTWSharp.fftwf.destroy_plan(fplan);
                            fplan = IntPtr.Zero;
                        }
                        fplan = FFTWSharp.fftwf.dft_1d(m_FFTSampleSize, pin, pout,
                            FFTWSharp.fftw_direction.Forward, FFTWSharp.fftw_flags.Estimate);
                    }

                    // this will fail with access denied
                    // unless you specify microphone 
                    // in the Package.appxmanifest
                    IAsyncOperation<CreateAudioDeviceInputNodeResult> result2 = null;
                    if (this.m_SelectedDevice != null)
                    {
                        result2 = this.m_AudioGraph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Other,
                            this.m_AudioGraph.EncodingProperties, this.m_SelectedDevice);
                    } else
                    {
                        result2 = this.m_AudioGraph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Other);
                    }

                    if (result2 != null)
                    {
                        result2.Completed = new AsyncOperationCompletedHandler<CreateAudioDeviceInputNodeResult>(OnCreateInputCompleted);
                    } else
                    {
                        ShowMessage($"Failed to create graph: {result.Status}");
                    }
                } else
                {
                    ShowMessage($"Failed to create graph: {result.Status}");
                }
            } else
            {
                ShowMessage($"Failed to create graph: {asyncStatus}");
            }
        }

        private void OnCreateInputCompleted(IAsyncOperation<CreateAudioDeviceInputNodeResult> asyncInfo, AsyncStatus asyncStatus)
        {
            if(asyncStatus == AsyncStatus.Completed)
            {
                CreateAudioDeviceInputNodeResult result = asyncInfo.GetResults();
                if(result.Status == AudioDeviceNodeCreationStatus.Success)
                {
                    this.m_AudioDevideInputNode = result.DeviceInputNode;
                    this.m_AudioFrameOutputNode = this.m_AudioGraph.CreateFrameOutputNode();
                    this.m_AudioDevideInputNode.AddOutgoingConnection(this.m_AudioFrameOutputNode);
                    this.m_AudioGraph.QuantumStarted += M_AudioGraph_QuantumStarted;
                    //this.m_AudioGraph.QuantumProcessed += M_AudioGraph_QuantumProcessed;
                    this.m_AudioGraph.Start();
                } else
                {
                    ShowMessage($"Failed to create audio device input node: {result.Status}");
                }
            } else
            {
                ShowMessage($"Failed to create audio device input node: {asyncStatus}");
            }
        }

        //unsafe private void M_AudioGraph_QuantumProcessed(AudioGraph sender, object args)
        //{
        //}

        unsafe private void M_AudioGraph_QuantumStarted(AudioGraph sender, object args)
        {
            // draw every n frames
            //if (fctr++ % 5 == 0)
            //{
                using (AudioFrame audioFrame = this.m_AudioFrameOutputNode.GetFrame())
                using (AudioBuffer audioBuffer = audioFrame.LockBuffer(AudioBufferAccessMode.Read))
                using (IMemoryBufferReference memBufferRef = audioBuffer.CreateReference())
                {
                    IMemoryBufferByteAccess byteAccess = memBufferRef as IMemoryBufferByteAccess;

                    byte* byteBuffer;
                    uint capacity;

                    byteAccess.GetBuffer(out byteBuffer, out capacity);

                    float* floatBuffer = (float*)byteBuffer;

                    for (int i = 0; i < this.m_AudioGraph.SamplesPerQuantum * this.m_AudioGraph.EncodingProperties.ChannelCount; i++)
                    {
                        this.m_QuantumSamples[i] = floatBuffer[i];
                    }

                    this.m_Capacity = capacity;
                    this.m_abCap = audioBuffer.Capacity;
                    this.m_abLen = audioBuffer.Length;
                }
                AudioCanvas.Invalidate();
            //}
        }

        protected override void OnNavigatedFrom(NavigationEventArgs args)
        {
            if (pin != IntPtr.Zero)
            {
                FFTWSharp.fftwf.free(pin);
                pin = IntPtr.Zero;
            }
            if (pout != IntPtr.Zero)
            {
                FFTWSharp.fftwf.free(pout);
                pout = IntPtr.Zero;
            }
            if (fplan != IntPtr.Zero)
            {
                FFTWSharp.fftwf.destroy_plan(fplan);
                fplan = IntPtr.Zero;
            }
            this.m_AudioGraph.Stop();
            m_AudioFrameOutputNode.Dispose();
            m_AudioDevideInputNode.Dispose();
            m_AudioGraph.Dispose();
            base.OnNavigatedFrom(args);
        }

        unsafe private void AudioCanvas_Draw(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender, 
            Microsoft.Graphics.Canvas.UI.Xaml.CanvasDrawEventArgs args)
        {
            if(this.m_AudioFrameOutputNode != null)
            {
                using (ICanvasBrush ybrush = new Microsoft.Graphics.Canvas.Brushes.CanvasSolidColorBrush(sender.Device, 
                    Windows.UI.Colors.Yellow))
                using (ICanvasBrush bbrush = new Microsoft.Graphics.Canvas.Brushes.CanvasSolidColorBrush(sender.Device,
                    Windows.UI.Colors.Red))
                {
                    // get a float pointer to the fftw in array
                    float* fpin = (float*)pin;

                    float halfHeight = (float)sender.ActualHeight / 2.0f;
                    for(int i=0; i<this.m_QuantumSamples.Length; i += 2)
                    {
                        int ii = i / 2;
                        float f = this.m_QuantumSamples[i];
                        if ((ii) < sender.ActualWidth)
                        {
                            args.DrawingSession.DrawLine(
                                (float)ii,
                                (float)halfHeight,
                                (float)ii,
                                (float)halfHeight + (f * halfHeight), ybrush);
                        }
                        // put the float value in the real part (even index)
                        // odd index is imaginery
                        fpin[i] = f;
                    }

                    FFTWSharp.fftw.execute(fplan);

                    float* fpout = (float*)pout;
                    double maxmag = 100;
                    double mult = this.m_AudioGraph.SamplesPerQuantum / maxmag;
                    int halfi = 0;
                    for (int i = 0; i < m_FFTSampleSize; i += 2)
                    {
                        double mag_k = 2.0 * Math.Sqrt(fpout[i] * fpout[i]) + (fpout[i + 1] * fpout[i + 1]) / (double)this.m_FFTSampleSize;
                        double a_k = 20.0 * Math.Log10(mag_k);
                        halfi = i / 2;
                        args.DrawingSession.DrawLine(0, halfi * 2, (float)(a_k * mult), halfi * 2, bbrush);
                        args.DrawingSession.DrawLine(0, halfi * 2 + 1, (float)(a_k * mult), halfi * 2 + 1, bbrush);
                    }
                }

                //args.DrawingSession.DrawText("Samples Per Quantum = " + this.m_AudioGraph.SamplesPerQuantum,
                    //0, 0, Windows.UI.Colors.Yellow);

                //args.DrawingSession.DrawText($"{this.m_FrameCount} "
                //    + $"spq {this.m_AudioGraph.SamplesPerQuantum} "
                //    + $"cap {this.m_Capacity} "
                //    + $"abcap {this.m_abCap} "
                //    + $"ablen {this.m_abLen}", 0, 0, Windows.UI.Colors.Yellow);
                //args.DrawingSession.DrawText($"bps {this.m_AudioGraph.EncodingProperties.BitsPerSample} "
                //    + $"ch {this.m_AudioGraph.EncodingProperties.ChannelCount} "
                //    + $"sr {this.m_AudioGraph.EncodingProperties.SampleRate}", 0, 18, Windows.UI.Colors.Yellow);
            }
        }

        private void btnChangeDevice_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}
