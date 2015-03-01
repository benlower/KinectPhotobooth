using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Toolkit
{
    namespace MotionDetector
    {

        public class RecordedValue
        {
            private static readonly int s_MaxNumFrames = 90;    // Maximum number of frames to store. This gives about 3 seconds worth of data.
                                                                // Higher numbers will take longer to process but will wait longer to reset

            private int m_currentFrame;                         // Index corresponding to the last frame recorded
            private int m_frameCount;                           // Total number of frames recorded
            private float[] m_frames = new float[s_MaxNumFrames];   // Circular buffer used to store the value history

            public RecordedValue() { Reset(); }

            public void Reset()
            {
                m_currentFrame = -1;
                m_frameCount = 0;
                Array.Clear(m_frames, 0, m_frames.Length);
            }

            public void RecordValue(float value)
            {
                m_currentFrame = (m_currentFrame + 1) % s_MaxNumFrames;
                if (m_frameCount < s_MaxNumFrames)
                {
                    ++(m_frameCount);
                }

                m_frames[m_currentFrame] = value;
            }

            public float GetValue(int numFramesAgo)
            {
                if (m_currentFrame < 0)
                {
                    return 0.0f;
                }

                int frameIndex = m_currentFrame - numFramesAgo;

                if (frameIndex < 0)
                {
                    frameIndex += m_frameCount;
                }

                if (frameIndex < 0 && frameIndex > s_MaxNumFrames)
                {
                    throw new System.IndexOutOfRangeException();
                }

                return m_frames[frameIndex];
            }

            public float CalcAverage(int numFramesToUse)
            {
                if (numFramesToUse > m_frameCount)
                {
                    numFramesToUse = m_frameCount;
                }

                if (numFramesToUse <= 0)
                {
                    return 0.0f;
                }

                float total = 0.0f;
                for (int i = 0; i < numFramesToUse; ++i)
                {
                    total += GetValue(i);
                }

                return total / numFramesToUse;
            }

            public float CalcStandardDeviation(int numFramesToUse)
            {
                if (numFramesToUse > m_frameCount)
                {
                    numFramesToUse = m_frameCount;
                }

                if (numFramesToUse == 0)
                {
                    return 0.0f;
                }

                float average = CalcAverage(numFramesToUse);

                float total = 0.0f;
                for (int i = 0; i < numFramesToUse; ++i)
                {
                    float delta = GetValue(i) - average;
                    total += delta * delta;
                }

                return (float)Math.Sqrt(total / numFramesToUse);
            }
        }

        public class MotionDetector
        {
            public MotionDetector()
            {
                m_motionPixelCount = 0;
                m_deltaZThreshold = 100;
                m_motionThreshold = 0.001f;
                m_noiseFloorResetFrameCount = 10;
                m_motionImageEnabled = false;
                Reset();
            }

            public void Reset()
            {
                m_motionAreaHistory.Reset();

                m_motionArea = 0.0f;
                m_motionAreaAverage = 0.0f;
                m_motionAreaStdDev = 0.0f;
                m_motionPixelCount = 0;
                m_lastFrameIndex = 0;
                m_lastLastFrameIndex = 1;

                Array.Clear(m_lastDepthImage, 0, m_lastDepthImage.Length);
                Array.Clear(m_motionImage, 0, m_motionImage.Length);
            }

            public void Update( int playerIndex, UInt16[] depthImage, byte[] bodyIndexImage )
            {
                if (depthImage == null || bodyIndexImage == null)
                {
                    return;
                }

                float motionArea = 0.0f;
                m_motionPixelCount = 0;

                if (m_motionImageEnabled)
                {
                    Array.Clear(m_motionImage, 0, m_motionImage.Length);
                }

                for (int row = 0; row < s_Height; row += 2)
                {
                    for (int col = 0; col < s_Width; col += 2)
                    {
                        int sourcePixelOffset = s_Width * row + col;
                        int storedPixelOffset = s_Width * row / 2 + col;
                        byte index = bodyIndexImage[sourcePixelOffset];

                        if (playerIndex == index)
                        {
                            UInt16 depth = depthImage[sourcePixelOffset];
                            UInt16 lastDepth = m_lastDepthImage[storedPixelOffset + m_lastFrameIndex];
                            UInt16 lastLastDepth = m_lastDepthImage[storedPixelOffset + m_lastLastFrameIndex];

                            if (lastLastDepth != 0 && lastDepth != 0 && depth != 0)
                            {
                                int delta = depth - lastDepth;
                                int lastDelta = lastDepth - lastLastDepth;

                                if (Math.Abs(delta + lastDelta) > m_deltaZThreshold)
                                {
                                    ++m_motionPixelCount;

                                    // Inverse of the approximate focal length of the sensor, derived from the vertical FOV
                                    float inverseFocalLength = 0.0027089166f;

                                    float voxelWidth = (depth / 1000.0f) * inverseFocalLength;
                                    motionArea += voxelWidth * voxelWidth;

                                    if (m_motionImageEnabled)
                                    {
                                        m_motionImage[storedPixelOffset] = true;
                                    }
                                }
                            }

                            m_lastDepthImage[storedPixelOffset + m_lastLastFrameIndex] = depth;
                        }
                    }
                }

                UpdateAreaAverageAndStdDev(motionArea);

                m_lastFrameIndex = (m_lastFrameIndex + 1) % 2;
                m_lastLastFrameIndex = (m_lastLastFrameIndex + 1) % 2;
            }
            
            public bool DidPlayerMove()
            { 
                return m_motionArea > ( m_motionAreaAverage + m_motionAreaStdDev * 2.0f + m_motionThreshold ); 
            }

            // Intermediate value access for debugging and tuning.
            public bool DidPixelMove(int row, int col)
            {
                if (row % 2 != 0 || col % 2 != 0)
                {
                    return false;
                }

                return m_motionImage[s_Width * row / 2 + col];
            }

            public void EnableMotionImage( bool enable ) { m_motionImageEnabled = enable; }
            public uint GetMotionPixelCount() { return m_motionPixelCount; }
            public float GetMotionArea() { return m_motionArea; }
            public float GetMotionAreaAverage() { return m_motionAreaAverage; }
            public float GetMotionAreaStdDev() { return m_motionAreaStdDev; }
            public UInt16 GetDeltaZThreshold() { return m_deltaZThreshold; }
            public void SetDeltaZThreshold( UInt16 deltaZThreshold ) { m_deltaZThreshold = deltaZThreshold; }
            public float GetMotionThreshold() { return m_motionThreshold; }
            public void SetMotionThreshold( float motionThreshold ) { m_motionThreshold = motionThreshold; }
            public uint GetNoiseFloorResetFrameCount() { return m_noiseFloorResetFrameCount; }
            public void SetNoiseFloorResetFrameCount( uint noiseFloorResetFrameCount ) { m_noiseFloorResetFrameCount = noiseFloorResetFrameCount; }

            static readonly int s_Width  = 512;
            static readonly int s_Height = 424;
            static readonly int s_NumPixels = s_Width * s_Height;

            private UInt16 m_deltaZThreshold;           // Cumulative amount of depth delta (in mm) that must be exceeded during 2 frames for a pixel to be considered
                                                        // "in motion" - default is 100 (10cm) Increasing this value will require faster motions to trigger the detector

            private float m_motionThreshold;            // Amount of area (in meters squared) that needs to be in motion to trigger the detector - default is 0.001
                                                        // Increasing this value will require more pixels in motion (more of your body moving) to trigger the detector

            private uint m_noiseFloorResetFrameCount;   // Number of low motion frames required to reset the noise floor to a new low - default is 10
                                                        // Increasing this value will increase the amount of "holding still" time required between motions

            private bool m_motionImageEnabled;          // Indicates whether the motion image is enabled


            private RecordedValue m_motionAreaHistory = new RecordedValue();  // Stores a history of the motion area for the last N frames

            private float m_motionArea;                 // Current motion area
            private float m_motionAreaAverage;          // Average motion area, used to define the motion noise floor
            private float m_motionAreaStdDev;           // Motion area standard deviation, also used define the motion noise floor

            private uint m_motionPixelCount;            // Number of pixels "in motion" on the last update

            private uint m_lastFrameIndex;              // Offset into m_lastDepthImage representing the last depth frame
            private uint m_lastLastFrameIndex;          // Offset into m_lastDepthImage representing the depth 2 frames ago

            private UInt16[] m_lastDepthImage = new UInt16[s_NumPixels / 2]; // Stores 2 frames of depth data at half width and height (update only evaluates every other pixel in width and height)
                                                    // Pixels are interleaved, so index 0 is the first pixel of one image, and index 1 is the first pixel of the other image

            private bool[] m_motionImage = new bool[s_NumPixels / 2];   // Stores the motion image at half width and height, pixels "in motion" on the last update are marked 'true' if m_motionImageEnabled is 'true'


            private void UpdateAreaAverageAndStdDev(float currentArea)
            {
                m_motionAreaHistory.RecordValue(currentArea);
                m_motionArea = currentArea;

                float longAreaAverage = m_motionAreaHistory.CalcAverage(30);
                float longAreaStdDev = m_motionAreaHistory.CalcStandardDeviation(30);
                float shortAreaAverage = m_motionAreaHistory.CalcAverage((int)m_noiseFloorResetFrameCount);
                float shortStdDev = m_motionAreaHistory.CalcStandardDeviation((int)m_noiseFloorResetFrameCount);

                if (shortAreaAverage + shortStdDev < m_motionAreaAverage)
                {
                    m_motionAreaAverage = shortAreaAverage;
                    m_motionAreaStdDev = shortStdDev;
                }
                else if (longAreaAverage > m_motionAreaAverage)
                {
                    float growthDampener = .01f;
                    m_motionAreaAverage += growthDampener * (longAreaAverage - m_motionAreaAverage);
                    m_motionAreaStdDev += growthDampener * (longAreaStdDev - m_motionAreaStdDev);
                }
                else
                {
                    m_motionAreaAverage = longAreaAverage;
                    m_motionAreaStdDev = longAreaStdDev;
                }
            }

        }

    }
}
