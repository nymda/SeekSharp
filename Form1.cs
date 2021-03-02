/*
Copyright (c) 2014 Stephen Stair (sgstair@akkit.org)
Additional code Miguel Parra (miguelvp@msn.com)

Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
 all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 THE SOFTWARE.
*/

// 
// the winusbdotnet repo isn't the correct place for this code long term
// Code is here for now for convenience in testing and iteration while being developed.
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.IO;

using winusbdotnet.UsbDevices;
using System.Drawing.Drawing2D;
using System.Diagnostics;

namespace TestSeek
{
    public partial class Form1 : Form
    {
        String localPath = @"c:\seek\";
        SeekThermal thermal;
        Thread thermalThread;
        int frameCount;
        bool stopThread;
        bool m_get_extra_cal;
        bool usignExternalCal;
        bool firstAfterCal;
        bool autoSaveImg;
        bool saveExternalFrames;
        Outshow os;

        ThermalFrame lastFrame, lastCalibrationFrame, lastReferenceFrame;
        ThermalFrame frameID4, frameID1;
        CalibratedThermalFrame lastUsableFrame, lastRenderedFrame;

        bool[] BadPixelArr = new bool[32448];//32448
        double[] gainCalArr = new double[32448];//32448
        int[] offsetCalArr = new int[32448];//32448
        Bitmap paletteImg;

        Queue<Bitmap> bmpQueue;

        public Form1()
        {
            InitializeComponent();

            DoubleBuffered = true;
            bmpQueue = new Queue<Bitmap>();

            paletteImg = (Bitmap)Properties.Resources.ResourceManager.GetObject("iron1000"); ;

            // Init button trigger to be off.
            m_get_extra_cal = false;
            usignExternalCal = false;
            firstAfterCal = false;
            autoSaveImg = false;
            saveExternalFrames = false;

            var device = SeekThermal.Enumerate().FirstOrDefault();
            if(device == null)
            {
                MessageBox.Show("No Seek Thermal devices found.\n(did you install the drivers?)");
                return;
            }
            thermal = new SeekThermal(device);

            thermalThread = new Thread(ThermalThreadProc);
            thermalThread.IsBackground = true;
            thermalThread.Start();
        }

        void ThermalThreadProc()
        {
            BinaryWriter tw;
            DateTime currentFrameTime = DateTime.Now;
            DateTime previousFrameTime = currentFrameTime;
            DateTime currentTime = DateTime.Now;
            DateTime previousTime = currentTime;
            int framesToCapture = 100;

            // Initialize frame (1 based)
            frameCount = 1;

            // Create the output files to save first 20 frames and associated metadata.
            //bw = new BinaryWriter(new FileStream("data.dat", FileMode.Create));
            tw = new BinaryWriter(new FileStream("data.txt", FileMode.Create));

            while (!stopThread && thermal != null)
            {
                bool progress = false;

                // Get frame
                lastFrame = thermal.GetFrameBlocking(Application.StartupPath + "/FRAMEBUFFER.bin");

                // Keep the ID4 and ID1 frame
                switch (lastFrame.StatusByte)
                {
                    case 1://shutter cal
                        frameID1 = lastFrame;
                        firstAfterCal = true;
                        break;
                    case 4://first frame gain cal
                        frameID4 = lastFrame;
                        break;
                    default:
                        break;
                }

                // Time after frame capture
                previousTime = currentTime;
                currentTime = DateTime.Now;

                // Save data and metadata for the first framesToCapture frames
                if (frameCount <= framesToCapture)
                {
                    tw.Write(Encoding.ASCII.GetBytes(String.Format("Frame {0} ID {1}\n", frameCount, lastFrame.RawDataU16[10])));
                    tw.Write(Encoding.ASCII.GetBytes(String.Format(lastFrame.AvgValue.ToString())));
                    tw.Write(Encoding.ASCII.GetBytes(String.Format("\n")));

                    if (frameCount == framesToCapture)
                    {
                        tw.Close();
                    }
                }

                switch (lastFrame.StatusByte)
                {
                    case 4://prvi frame za izračuna gaina
                        markBadPixels();
                        getGainCalibration();
                        //konec: prvi frame za izračuna gaina
                        break;
                    case 1://shutter frame za izračun offseta
                        markBadPixels();
                        applyGainCalibration();
                        if (!usignExternalCal) getOffsetCalibration();
                        lastCalibrationFrame = frameID1;
                        saveExternalFrames = false;
                        //konec: shutter frame
                        break;
                    case 3://pravi slikovni frame
                        markZeroPixels();
                        applyGainCalibration();

                        if (m_get_extra_cal)//if this pixel should be used as reference
                        {
                            m_get_extra_cal = false;
                            usignExternalCal = true;
                            getOffsetCalibration();
                            saveExternalFrames = true;
                        }

                        applyOffsetCalibration();
                        fixBadPixels();
                        lastUsableFrame = lastFrame.ProcessFrameU16(lastReferenceFrame, frameID4);
                        progress = true;
                        //konec: pravi slikovni frame
                        break;
                    default:
                        break;
                }

                // Increase frame count.
                frameCount++;

                if(progress)
                {
                    Invalidate();//ponovno izriši formo...
                }
            }
        }

        private void markBadPixels()
        {
            ushort[] RawDataArr = lastFrame.RawDataU16;

            for(int i=0;i<RawDataArr.Length;i++)
            {
                if ((RawDataArr[i] < 1000) || (RawDataArr[i] > (12000)))
                {
                    BadPixelArr[i] = true;
                }
            }
        }

        private void markZeroPixels()
        {
            ushort[] RawDataArr = lastFrame.RawDataU16;

            for (int i = 0; i < RawDataArr.Length; i++)
            {
                if (RawDataArr[i]==0)
                {
                    BadPixelArr[i] = true;
                }
            }
        }

        private void getGainCalibration()
        {
            //gainCalArr
            ushort[] RawDataArr = lastFrame.RawDataU16;

            for (int i = 0; i < RawDataArr.Length; i++)
            {
                if ((RawDataArr[i] >= 1000) && (RawDataArr[i] <= (12000))){
                    gainCalArr[i] = (double)lastFrame.AvgValue / (double)RawDataArr[i];
                }
                else {
                    gainCalArr[i] = 1;
                }
            }
        }

        private void applyGainCalibration()
        {
            //gainCalArr
            ushort[] RawDataArr = lastFrame.RawDataU16;

            for (int i = 0; i < RawDataArr.Length; i++)
            {
                if (!BadPixelArr[i])
                {
                    lastFrame.RawDataU16[i] = (ushort)(RawDataArr[i] * gainCalArr[i]);
                }
            }
        }

        private void getOffsetCalibration()
        {
            //offsetCalArr
            ushort[] RawDataArr = lastFrame.RawDataU16;

            for (int i = 0; i < RawDataArr.Length; i++)
            {
                if (!BadPixelArr[i])
                {
                    offsetCalArr[i] = lastFrame.AvgValue - RawDataArr[i];
                }
            }
        }

        private void applyOffsetCalibration()
        {
            //offsetCalArr
            ushort[] RawDataArr = lastFrame.RawDataU16;

            for (int i = 0; i < RawDataArr.Length; i++)
            {
                if (!BadPixelArr[i])
                {
                    lastFrame.RawDataU16[i] =(ushort)(RawDataArr[i] + offsetCalArr[i]);
                }
            }
        }


        private void fixBadPixels()
        {
            int i = 0;
            ushort[] RawDataArr = lastFrame.RawDataU16;

            int[,] frame_pixels = new int[208, 156];

            for (int y = 0; y < 156; ++y)
            {
                for (int x = 0; x < 208; ++x, ++i)
                {
                    frame_pixels[x, y] = RawDataArr[i];
                }
            }
            i = 0;
            int avgVal = 0;
            int[] arrColor = new int[4];
            for (int y = 0; y < 156; y++)
            {
                for (int x = 0; x < 208; x++)
                {

                    if (x > 0 && x < 207 && y > 0 && y < 155)
                    {
                        arrColor[0] = frame_pixels[x, y - 1];//top
                        arrColor[1] = frame_pixels[x, y + 1];//bottom
                        arrColor[2] = frame_pixels[x - 1, y];//left
                        arrColor[3] = frame_pixels[x + 1, y];//right

                        //get average value, but exclude neighbour dead pixels from average:
                        avgVal = (arrColor.Sum() - (arrColor.Min() + arrColor.Max())) / 2;

                        if (BadPixelArr[i] || lastFrame.RawDataU16[i]==0)//if its bad pixel or if val == 0
                        {
                            lastFrame.RawDataU16[i] = (ushort)avgVal;
                            frame_pixels[x, y] = avgVal;
                        }

                    }
                    i++;
                }
            }

            i = 0;
            avgVal = 0;

            for (int y = 0; y < 156; y++)
            {
                for (int x = 0; x < 208; x++)
                {

                    if (x > 0 && x < 207 && y > 0 && y < 155)
                    {
                        arrColor[0] = frame_pixels[x, y - 1];//top
                        arrColor[1] = frame_pixels[x, y + 1];//bottom
                        arrColor[2] = frame_pixels[x - 1, y];//left
                        arrColor[3] = frame_pixels[x + 1, y];//right

                        //get average value, but exclude neighbour dead pixels from average:
                        avgVal = (arrColor.Sum() - (arrColor.Min() + arrColor.Max())) / 2;

                        if (Math.Abs(avgVal - frame_pixels[x, y]) > 100 && avgVal != 0)//if its bad pixel or if val dif is to big to near pixels
                        {
                            lastFrame.RawDataU16[i] = (ushort)avgVal;
                        }
                    }
                    i++;
                }
            }

            arrColor = new int[3];
            //fix first line:
            for (int x = 1; x < 207; x++)
            {
                arrColor[0] = frame_pixels[x, 1];//bottom
                arrColor[1] = frame_pixels[x - 1, 0];//left
                arrColor[2] = frame_pixels[x + 1, 0];//right

                avgVal = (arrColor.Sum() - (arrColor.Min() + arrColor.Max()));

                if ((Math.Abs(avgVal - frame_pixels[x, 0]) > 100) && avgVal != 0)//if val diff is to big to near pixels, then fix it
                {
                    lastFrame.RawDataU16[x] = (ushort)avgVal;
                }
            }

            //fix last line:
            for (int x = 1; x < 206; x++)
            {
                arrColor[0] = frame_pixels[x, 154];//top
                arrColor[1] = frame_pixels[x - 1, 155];//left
                arrColor[2] = frame_pixels[x + 1, 155];//right

                avgVal = (arrColor.Sum() - (arrColor.Min() + arrColor.Max()));

                if ((Math.Abs(avgVal - frame_pixels[x, 155]) > 100) && avgVal != 0)//if val diff is to big to near pixels, then fix it
                {
                    lastFrame.RawDataU16[155 * 208 + x] = (ushort)avgVal;//32240
                }
            }

            //fix first column
            for (int y = 1; y < 155; y++)
            {
                arrColor[0] = frame_pixels[0, y - 1];//top
                arrColor[1] = frame_pixels[1, y];//right
                arrColor[2] = frame_pixels[0,y+1];//bottom

                avgVal = (arrColor.Sum() - (arrColor.Min() + arrColor.Max()));

                if ((Math.Abs(avgVal - frame_pixels[0, y]) > 100) && avgVal != 0)//if val diff is to big to near pixels, then fix it
                {
                    lastFrame.RawDataU16[y * 208] = (ushort)avgVal;
                }
            }
        }

        

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            stopThread = true;
            if (thermal != null)
            {
                thermalThread.Join(500);
                thermal.Deinit();
            }
        }

        public struct palette
        {
            public int r, g, b;
            public palette(int ri, int gi, int bi)
            {
                r = ri;
                g = gi;
                b = bi;
            }
        }

        Bitmap tmp;

        int frame = 0;
        int paletteIndex = 0;
        Bitmap bmp = null;
        Bitmap bigImage = null;
        Graphics bmGr;
        Graphics gr;
        ushort maxValue = 0;
        ushort minValue = 12000;

        void drawThermalFrame()
        {
            CalibratedThermalFrame data = lastUsableFrame;
            if (data == null) return;
            int y;
            if (data != lastRenderedFrame)
            {
                lastRenderedFrame = data;
                // Process new frame
                if(bmp == null)
                {
                    bmp = new Bitmap((data.Width - 2), data.Height);
                    bigImage = new Bitmap(412, 312);
                    bmGr = Graphics.FromImage(bmp);
                    gr = Graphics.FromImage(bigImage);
                }

                bmGr.FillRectangle(Brushes.White, 0, 0, bmp.Width, bmp.Height);

                int c = 0;
                int v;


                //button2.Text = "Automatic";
                if (!checkBox4.Checked)
                {
                    maxValue = (ushort)trackBar1.Value;
                    minValue = (ushort)trackBar2.Value;
                }

                int high_x = -1;
                int high_y = -1;
                int high_v = 0;

                int low_x = -1;
                int low_y = -1;
                int low_v = 12000;

                //draw the image pixel by pixel
                for (y = 1; y < 154; y++)
                {
                    for (int x = 0; x < 206; x++)
                    {
                        v = data.PixelData[y * 208 + x]; // + data.PixelData[y * 208 + 206] / 10; // no need to use column 207 since we already use frame id 4, max/min will be off if uncommented as well.
                        //Console.WriteLine(x + " : " + y + " : " + v);

                        if(v > high_v)
                        {
                            high_v = v;
                            high_x = x;
                            high_y = y;
                        }

                        if (v < low_v)
                        {
                            low_v = v;
                            low_x = x;
                            low_y = y;
                        }

                        // Scale data to be within [0-255] for LUT mapping.
                        ushort maxmin = maxValue;
                        maxmin -= minValue;
                        // Avoid divide by 0
                        if (maxmin == 0)
                            maxmin = 1;

                        v = (v - minValue) * 999 / maxmin;
                        if (v < 0)
                            v = 0;
                        if (v > 999)
                            v = 999;

                        // Greyscale output (would always be limited to 256 colors)
                        bmp.SetPixel(x, y, paletteImg.GetPixel(v, paletteIndex));
                    }
                }

                if (checkBox4.Checked)
                {
                    maxValue = (ushort)((ushort)high_v + numericUpDown1.Value);
                    minValue = (ushort)((ushort)low_v - numericUpDown1.Value);
                    trackBar1.Value = high_v;
                    trackBar2.Value = low_v;
                }


                if (high_x != -1 && checkBox2.Checked)
                {
                    bmGr.DrawLine(Pens.Red, new Point(high_x, high_y + 5), new Point(high_x, high_y - 5));
                    bmGr.DrawLine(Pens.Red, new Point(high_x + 5, high_y), new Point(high_x - 5, high_y));
                }

                if (low_x != -1 && checkBox3.Checked)
                {
                    Console.WriteLine(low_x + ":" + low_y);
                    bmGr.DrawLine(Pens.Blue, new Point(low_x, low_y + 5), new Point(low_x, low_y - 5));
                    bmGr.DrawLine(Pens.Blue, new Point(low_x + 5, low_y), new Point(low_x - 5, low_y));
                }

                gr.SmoothingMode = SmoothingMode.HighQuality;
                gr.InterpolationMode = InterpolationMode.HighQualityBilinear;
                gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gr.DrawImage(bmp, new Rectangle(0, 0, 412, 312));

                //bigImage = new Bitmap(bmp, new Size(412, 312));
                // Queue Image for display
                bmpQueue.Enqueue(bigImage);
                if (bmpQueue.Count > 1) bmpQueue.Dequeue();

                frame++;
                if (checkBox1.Checked)
                {
                    long time = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
                    if (!Directory.Exists(Application.StartupPath + "/VIDEO")) { Directory.CreateDirectory(Application.StartupPath + "/VIDEO"); }
                    if(!File.Exists(Application.StartupPath + "/VIDEO/render.bat")){
                        string bScript = @"@echo off
                            set rndName=%random%%random%
                            set path={0}
                            c:\ffmpeg -framerate 8 -i THERMAL_%%d.png %path%\video_%rndName%.mp4
                            echo %path%\video_%rndName%.mp4
                            start %path%\video_%rndName%.mp4
                            pause";

                        bScript = string.Format(bScript, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
                        File.WriteAllText((Application.StartupPath + "/VIDEO/render.bat"), bScript);
                    }
                    string path = Application.StartupPath + "/VIDEO/THERMAL_" + frame.ToString() + ".png";
                    pictureBox2.Image.Save(path);
                }
            }

            y = 10;
            foreach (Bitmap b in bmpQueue.Reverse())
            {
                //b.RotateFlip(RotateFlipType.Rotate90FlipNone);
                tmp = (Bitmap)b.Clone();
                tmp.RotateFlip((RotateFlipType)Math.Floor((decimal)(comboBox1.SelectedIndex)));
                pictureBox2.Image = tmp;
                //os.pictureBox1.Image = tmp;
                y += b.Height + 10;
            }
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox1.Checked)
            {
                frame = 0;
            }
        }

        private void numericUpDown1_ValueChanged(object sender, EventArgs e)
        {

        }

        private void comboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {
            paletteIndex = comboBox2.SelectedIndex;
            Rectangle space = new Rectangle(0, (int)comboBox2.SelectedIndex, 1001, 1);
            Bitmap currentPalette = paletteImg.Clone(space, System.Drawing.Imaging.PixelFormat.DontCare);
            currentPalette.RotateFlip(RotateFlipType.Rotate270FlipNone);

            Bitmap cvs = new Bitmap(21, 500);
            Graphics tmp = Graphics.FromImage(cvs);
            tmp.InterpolationMode = InterpolationMode.NearestNeighbor;
            tmp.DrawImage(currentPalette, 0, 0, 51, 500);

            pictureBox3.Image = cvs;
        }

        private void checkBox4_CheckedChanged(object sender, EventArgs e)
        {
            numericUpDown1.Enabled = checkBox4.Checked;
        }

        // Button to capture external reference or switch to internal shutter.
        private void button1_Click(object sender, EventArgs e)
        {
            m_get_extra_cal = true;
        }

        // Button to toggle between automatic ranging or manual.
        private void button2_Click(object sender, EventArgs e)
        {
            long time = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
            string path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) + "/THERMAL_" + time.ToString() + ".png";
            pictureBox2.Image.Save(path);
            Process.Start(path);
        }

        // Not needed events.
        private void Form1_Load(object sender, EventArgs e)
        {
            comboBox1.SelectedIndex = 4;
            comboBox2.SelectedIndex = 0;
            comboBox2_SelectedIndexChanged(null, null);
            timer1.Start();
        }

        Stopwatch st = new Stopwatch();
        private void timer1_Tick(object sender, EventArgs e)
        {
            if(os == null)
            {
                //os = new Outshow();
                //os.Show();
            }
            st.Start();
            drawThermalFrame();
            st.Stop();
            Console.WriteLine("Frame time: " + st.ElapsedMilliseconds);
            st.Reset();
        }
    }
}
