package io.ref12.medusa

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.os.Build
import android.provider.Settings as AndroidSettings
import java.io.File
import java.io.IOException

/** PackageInstaller session installer (not the deprecated ACTION_INSTALL_PACKAGE intent). */
class PackageApkInstaller(private val ctx: Context) : ApkInstaller {
    override fun canInstall() = ctx.packageManager.canRequestPackageInstalls()

    override fun install(apk: File) {
        val pi = ctx.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        params.setAppPackageName(ctx.packageName)
        params.setSize(apk.length())
        // Android 12+: later updates can go through without a prompt when the system allows it.
        if (Build.VERSION.SDK_INT >= 31) params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        val id = pi.createSession(params)
        try {
            pi.openSession(id).use { s ->
                apk.inputStream().use { ins -> s.openWrite("medusa.apk", 0, apk.length()).use { out -> ins.copyTo(out); s.fsync(out) } }
                val intent = Intent(ctx, InstallResultReceiver::class.java).setAction(ACTION).setPackage(ctx.packageName)
                val pending = PendingIntent.getBroadcast(ctx, id, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
                s.commit(pending.intentSender)
            }
        } catch (e: Exception) {
            try { pi.abandonSession(id) } catch (x: Exception) { /* already gone */ }
            throw if (e is IOException) e else IOException(e.message ?: e.javaClass.simpleName)
        }
    }

    companion object {
        const val ACTION = "io.ref12.medusa.INSTALL_RESULT"

        /** The system screen where the user allows this app to install apps. */
        fun permissionIntent(ctx: Context) = Intent(AndroidSettings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + ctx.packageName))
    }
}

/** Receives the session result and hands it to [UpdateManager]. */
class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        context.app.updates.onInstallStatus(status, msg) {
            @Suppress("DEPRECATION")
            val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)
            confirm?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)?.let { context.startActivity(it) }
        }
    }
}
