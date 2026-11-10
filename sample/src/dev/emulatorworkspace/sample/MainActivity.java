package dev.emulatorworkspace.sample;

import android.app.Activity;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Bundle;
import android.os.SystemClock;
import android.util.Log;
import android.view.Choreographer;
import android.view.MotionEvent;
import android.view.View;

/**
 * Deterministic Phase 1 sample:
 * - tapping anywhere increments a counter and cycles a solid marker colour;
 * - every tap writes a structured WS_SAMPLE log line with the tap index;
 * - a Choreographer-driven circle provides a steady 60 Hz scene for capture
 *   and input-latency measurements.
 *
 * Marker geometry: a square centred at (width/2, height*0.30) with side
 * min(width,height)*0.22. The colour is palette[taps % palette.length].
 */
public final class MainActivity extends Activity {
    private static final String TAG = "WS_SAMPLE";
    private static final int[] PALETTE = {
            0xFFE53935, 0xFF43A047, 0xFF1E88E5,
            0xFFFDD835, 0xFF8E24AA, 0xFF00ACC1,
    };

    private SampleView view;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        view = new SampleView();
        setContentView(view);
        Log.i(TAG, "ACTIVITY_READY pid=" + android.os.Process.myPid());
    }

    @Override
    protected void onResume() {
        super.onResume();
        view.resume();
        Log.i(TAG, "ACTIVITY_RESUMED");
    }

    @Override
    protected void onPause() {
        view.pause();
        Log.i(TAG, "ACTIVITY_PAUSED taps=" + view.taps);
        super.onPause();
    }

    private final class SampleView extends View implements Choreographer.FrameCallback {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint text = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF marker = new RectF();
        private int taps;
        private long frames;
        private long fpsWindowStart = SystemClock.uptimeMillis();
        private int fps;
        private boolean running;
        private float phase;

        SampleView() {
            super(MainActivity.this);
            text.setColor(Color.WHITE);
            text.setTextAlign(Paint.Align.CENTER);
            setBackgroundColor(0xFF101418);
        }

        void resume() {
            if (!running) {
                running = true;
                Choreographer.getInstance().postFrameCallback(this);
                fpsWindowStart = SystemClock.uptimeMillis();
                frames = 0;
            }
        }

        void pause() {
            running = false;
            Choreographer.getInstance().removeFrameCallback(this);
        }

        @Override
        public void doFrame(long frameTimeNanos) {
            if (!running) {
                return;
            }

            frames++;
            long now = SystemClock.uptimeMillis();
            if (now - fpsWindowStart >= 1000) {
                fps = (int) (frames * 1000 / (now - fpsWindowStart));
                frames = 0;
                fpsWindowStart = now;
            }

            phase += 0.008f;
            if (phase > 1f) {
                phase -= 1f;
            }

            invalidate();
            Choreographer.getInstance().postFrameCallback(this);
        }

        @Override
        protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            int width = getWidth();
            int height = getHeight();

            text.setTextSize(height * 0.030f);
            text.setColor(0xFFB0BEC5);
            canvas.drawText("Emulator Workspace - Phase 1 sample", width / 2f, height * 0.10f, text);

            text.setTextSize(height * 0.055f);
            text.setColor(Color.WHITE);
            canvas.drawText("TAPS: " + taps, width / 2f, height * 0.18f, text);

            float side = Math.min(width, height) * 0.22f;
            float cx = width / 2f;
            float cy = height * 0.30f;
            marker.set(cx - side / 2f, cy - side / 2f, cx + side / 2f, cy + side / 2f);
            paint.setColor(PALETTE[taps % PALETTE.length]);
            canvas.drawRoundRect(marker, side * 0.08f, side * 0.08f, paint);

            float sweep = width * 0.70f;
            float x = width * 0.15f + sweep * (phase < 0.5f ? phase * 2f : (1f - phase) * 2f);
            paint.setColor(0xFF64B5F6);
            canvas.drawCircle(x, height * 0.62f, Math.min(width, height) * 0.055f, paint);

            text.setTextSize(height * 0.030f);
            text.setColor(0xFF90A4AE);
            canvas.drawText("rendered fps " + fps, width / 2f, height * 0.90f, text);
            canvas.drawText("tap anywhere", width / 2f, height * 0.94f, text);
        }

        @Override
        public boolean onTouchEvent(MotionEvent event) {
            if (event.getActionMasked() == MotionEvent.ACTION_UP) {
                taps++;
                long uptime = SystemClock.uptimeMillis();
                Log.i(TAG, "TAP index=" + taps + " x=" + (int) event.getX() + " y=" + (int) event.getY()
                        + " uptimeMs=" + uptime);
                invalidate();
            }

            return true;
        }
    }
}
