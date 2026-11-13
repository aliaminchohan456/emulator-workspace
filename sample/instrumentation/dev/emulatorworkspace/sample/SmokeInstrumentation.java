package dev.emulatorworkspace.sample;

import android.app.Activity;
import android.app.Instrumentation;
import android.os.Bundle;

public final class SmokeInstrumentation extends Instrumentation {
    @Override
    public void onCreate(Bundle arguments) {
        super.onCreate(arguments);
        start();
    }

    @Override
    public void onStart() {
        boolean passed = "dev.emulatorworkspace.sample".equals(getTargetContext().getPackageName())
                && getTargetContext().getPackageManager().getLaunchIntentForPackage(
                        getTargetContext().getPackageName()) != null;
        Bundle result = new Bundle();
        result.putInt("numtests", 1);
        result.putInt("numfailures", passed ? 0 : 1);
        result.putString("stream", "WS fixture checked its target package and launch activity");
        finish(passed ? Activity.RESULT_OK : Activity.RESULT_CANCELED, result);
    }
}
