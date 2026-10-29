import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val appVersion = (findProperty("versionName") as String?) ?: "1.3.2"
val keystoreFile: String? = System.getenv("MEDUSA_KEYSTORE_FILE")

android {
    namespace = "io.ref12.medusa"
    compileSdk = 36

    defaultConfig {
        applicationId = "io.ref12.medusa"
        minSdk = 26
        targetSdk = 36
        versionCode = (findProperty("versionCode") as String?)?.toInt() ?: 1
        versionName = appVersion
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64") }
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        if (keystoreFile != null) {
            create("release") {
                storeFile = file(keystoreFile)
                storePassword = System.getenv("MEDUSA_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("MEDUSA_KEY_ALIAS")
                keyPassword = System.getenv("MEDUSA_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (keystoreFile != null) signingConfig = signingConfigs.getByName("release")
        }
    }

    buildFeatures { buildConfig = true }

    // youtubedl-android needs its native libraries extracted to disk.
    packaging { jniLibs { useLegacyPackaging = true } }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    testOptions { unitTests.isReturnDefaultValues = true }
}

kotlin { compilerOptions { jvmTarget.set(JvmTarget.JVM_17) } }

android.applicationVariants.all {
    outputs.all {
        (this as com.android.build.gradle.internal.api.BaseVariantOutputImpl).outputFileName =
            if (buildType.name == "release") "medusa-$appVersion.apk"
            else "medusa-$appVersion-${buildType.name}.apk"
    }
}

dependencies {
    implementation("io.github.junkfood02.youtubedl-android:library:0.18.1")
    implementation("io.github.junkfood02.youtubedl-android:ffmpeg:0.18.1")
    implementation("org.nanohttpd:nanohttpd:2.3.1")
    implementation("androidx.media3:media3-exoplayer:1.4.1")
    implementation("androidx.media3:media3-ui:1.4.1")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")

    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
}
