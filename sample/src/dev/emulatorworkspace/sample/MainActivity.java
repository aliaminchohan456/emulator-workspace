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
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Deterministic Phase 2 fixture:
 * - login form with semantic content descriptions and a host HTTP fixture;
 * - a bounded 350 ms network timeout so an emulator delay profile is observable;
 * - FLAG_SECURE screen with a unique marker;
 * - the counter/marker/animated scene retained for input tests.
 */
public final class MainActivity extends Activity {
    private static final String TAG = "WS_SAMPLE";
    private static final int[] PALETTE = {
            0xFFE53935, 0xFF43A047, 0xFF1E88E5, 0xFFFDD835, 0xFF8E24AA, 0xFF00ACC1,
    };
    private static final AtomicInteger LOGIN_ATTEMPTS = new AtomicInteger();

    private SampleView sampleView;
    private EditText user;
    private EditText password;
    private TextView status;
    private TextView secureMarker;
    private LinearLayout loginPanel;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(0xFF101418);
        int pad = (int) (16 * getResources().getDisplayMetrics().density);
        root.setPadding(pad, pad, pad, pad);

        TextView title = new TextView(this);
        title.setText("Emulator Workspace QA fixture");
        title.setTextColor(Color.WHITE);
        title.setTextSize(18);
        root.addView(title);

        user = new EditText(this);
        user.setHint("user");
        user.setTextColor(Color.WHITE);
        user.setHintTextColor(0xFF90A4AE);
        user.setContentDescription("login_user");
        root.addView(user);

        password = new EditText(this);
        password.setHint("password");
        password.setTextColor(Color.WHITE);
        password.setHintTextColor(0xFF90A4AE);
        password.setContentDescription("login_password");
        root.addView(password);

        Button signIn = new Button(this);
        signIn.setText("Sign in");
        signIn.setContentDescription("login_button");
        signIn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                attemptLogin();
            }
        });
        root.addView(signIn);

        status = new TextView(this);
        status.setText("SIGNED OUT");
        status.setTextColor(0xFF90A4AE);
        status.setContentDescription("login_status");
        root.addView(status);

        LinearLayout secureRow = new LinearLayout(this);
        secureRow.setOrientation(LinearLayout.HORIZONTAL);

        Button secure = new Button(this);
        secure.setText("Secure note");
        secure.setContentDescription("secure_button");
        secure.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                setSecure(true);
            }
        });
        secureRow.addView(secure);

        Button clear = new Button(this);
        clear.setText("Clear secure");
        clear.setContentDescription("clear_secure");
        clear.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                setSecure(false);
            }
        });
        secureRow.addView(clear);
        root.addView(secureRow);

        secureMarker = new TextView(this);
        secureMarker.setText("SECRET-MARKER-NONE");
        secureMarker.setTextColor(Color.WHITE);
        secureMarker.setContentDescription("secure_marker");
        root.addView(secureMarker);

        loginPanel = new LinearLayout(this);
        loginPanel.setOrientation(LinearLayout.VERTICAL);
        loginPanel.setContentDescription("login_panel");
        root.addView(loginPanel);

        sampleView = new SampleView();
        LinearLayout.LayoutParams sampleParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f);
        root.addView(sampleView, sampleParams);

        setContentView(root);
        Log.i(TAG, "ACTIVITY_READY pid=" + android.os.Process.myPid());
    }

    private void setSecure(boolean enabled) {
        if (enabled) {
            getWindow().setFlags(WindowManager.LayoutParams.FLAG_SECURE,
                    WindowManager.LayoutParams.FLAG_SECURE);
            String marker = "SECRET-MARKER-" + LOGIN_ATTEMPTS.incrementAndGet();
            secureMarker.setText(marker);
            Log.i(TAG, "SECURE_ON " + marker);
        } else {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_SECURE);
            secureMarker.setText("SECRET-MARKER-NONE");
            Log.i(TAG, "SECURE_OFF");
        }
    }

    private void attemptLogin() {
        status.setText("SIGNING IN");
        final String userValue = user.getText().toString();
        final String passValue = password.getText().toString();
        new Thread(new Runnable() {
            @Override
            public void run() {
            String result;
            try {
                String url = "http://10.0.2.2:8765/login?user="
                        + URLEncoder.encode(userValue, "UTF-8")
                        + "&pass=" + URLEncoder.encode(passValue, "UTF-8");
                HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection();
                connection.setConnectTimeout(1500);
                connection.setReadTimeout(1500);
                connection.setRequestMethod("GET");
                int code = connection.getResponseCode();
                InputStream stream = code >= 200 && code < 300
                        ? connection.getInputStream() : connection.getErrorStream();
                if (stream != null) {
                    stream.close();
                }
                connection.disconnect();
                result = code == 200 ? "WELCOME" : "LOGIN_FAILED_" + code;
            } catch (Exception ex) {
                result = "NETWORK TIMEOUT";
            }

            final String finalResult = result;
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    status.setText(finalResult);
                    Log.i(TAG, "LOGIN_RESULT " + finalResult + " user=" + userValue);
                }
            });
            }
        }, "ws-login").start();
    }

    @Override
    protected void onResume() {
        super.onResume();
        sampleView.resume();
    }

    @Override
    protected void onPause() {
        sampleView.pause();
        super.onPause();
    }

    private final class SampleView extends View implements Choreographer.FrameCallback {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint text = new Paint(Paint.ANTI_ALIAS_FLAG);
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
            setContentDescription("sample_canvas");
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

            text.setTextSize(height * 0.050f);
            text.setColor(Color.WHITE);
            canvas.drawText("TAPS: " + taps, width / 2f, height * 0.16f, text);

            float side = Math.min(width, height) * 0.20f;
            float cx = width / 2f;
            float cy = height * 0.30f;
            RectF marker = new RectF(cx - side / 2f, cy - side / 2f, cx + side / 2f, cy + side / 2f);
            paint.setColor(PALETTE[taps % PALETTE.length]);
            canvas.drawRoundRect(marker, side * 0.08f, side * 0.08f, paint);

            float sweep = width * 0.70f;
            float x = width * 0.15f + sweep * (phase < 0.5f ? phase * 2f : (1f - phase) * 2f);
            paint.setColor(0xFF64B5F6);
            canvas.drawCircle(x, height * 0.56f, Math.min(width, height) * 0.05f, paint);

            text.setTextSize(height * 0.040f);
            text.setColor(0xFF90A4AE);
            canvas.drawText("rendered fps " + fps, width / 2f, height * 0.90f, text);
        }

        @Override
        public boolean onTouchEvent(MotionEvent event) {
            if (event.getActionMasked() == MotionEvent.ACTION_UP) {
                taps++;
                Log.i(TAG, "TAP index=" + taps + " x=" + (int) event.getX() + " y=" + (int) event.getY()
                        + " uptimeMs=" + SystemClock.uptimeMillis());
                invalidate();
            }

            return true;
        }
    }
}
