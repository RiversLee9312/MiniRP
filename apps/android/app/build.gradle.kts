import javax.inject.Inject

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

val openUiSource = rootProject.layout.projectDirectory.dir("genui")
val npm = if (System.getProperty("os.name").startsWith("Windows"))
    listOf("cmd.exe", "/d", "/c", "npm.cmd") else listOf("npm")

abstract class BuildOpenUiAssets : DefaultTask() {
    @get:Internal abstract val sourceDirectory: DirectoryProperty
    @get:InputFiles @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val sourceFiles: ConfigurableFileCollection
    @get:OutputDirectory abstract val outputDirectory: DirectoryProperty
    @get:Inject abstract val execOperations: ExecOperations
    @get:Inject abstract val fileSystemOperations: FileSystemOperations

    @TaskAction
    fun bundle() {
        val destination = outputDirectory.get()
        fileSystemOperations.delete { delete(destination.dir("openui")) }
        execOperations.exec {
            workingDir(sourceDirectory.get().asFile)
            commandLine("node", "build.mjs", destination.asFile.absolutePath)
        }
    }
}

val installOpenUiDependencies by tasks.registering(Exec::class) {
    workingDir(openUiSource.asFile)
    inputs.files(openUiSource.file("package.json"), openUiSource.file("package-lock.json"))
    outputs.file(openUiSource.file("node_modules/.package-lock.json"))
    commandLine(npm + listOf("ci", "--ignore-scripts", "--no-audit", "--no-fund"))
}

val buildOpenUiAssets by tasks.registering(BuildOpenUiAssets::class) {
    dependsOn(installOpenUiDependencies)
    sourceDirectory.set(openUiSource)
    sourceFiles.from(fileTree(openUiSource).matching { exclude("node_modules/**", "dist/**") })
    outputDirectory.set(layout.buildDirectory.dir("generated/openui/assets"))
}

androidComponents.onVariants { variant ->
    variant.sources.assets?.addGeneratedSourceDirectory(buildOpenUiAssets, BuildOpenUiAssets::outputDirectory)
}

android {
    namespace = "com.minirp.app"
    compileSdk = 37
    ndkVersion = "28.2.13676358"
    defaultConfig {
        // Android uses the application ID and signing key to preserve installed data on upgrade.
        applicationId = "com.minichat.app"
        minSdk = 26
        targetSdk = 36
        versionCode = 4
        versionName = "1.0.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk { abiFilters += "arm64-v8a" }
        externalNativeBuild { cmake { arguments += "-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON" } }
    }
    buildFeatures { compose = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_21
        targetCompatibility = JavaVersion.VERSION_21
    }
    androidResources { generateLocaleConfig = true }
    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }
    packaging {
        jniLibs {
            useLegacyPackaging = false
            // Older local builds may still have this generated binary in jniLibs.
            excludes += "**/libminichatcore.so"
        }
        resources { merges += "META-INF/LICENSE.md" }
    }
    buildTypes {
        debug {
            val isolatedTest =
                providers.gradleProperty("minirp.isolatedTest")
                    .orElse(providers.gradleProperty("minichat.isolatedTest"))
            if (isolatedTest.orNull == "true") {
                applicationIdSuffix = ".qa"
            }
        }
        release { isMinifyEnabled = false }
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2026.05.01"))
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.appcompat:appcompat:1.7.1")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3:1.5.0-rc01")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.10.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.10.0")
    implementation("androidx.datastore:datastore-preferences:1.3.0")
    implementation("androidx.webkit:webkit:1.15.0")
    implementation("com.squareup.okhttp3:okhttp:5.3.2")
    implementation("com.mikepenz:multiplatform-markdown-renderer-m3:0.43.0")
    implementation("androidx.paging:paging-compose:3.5.3")
    implementation("androidx.navigation:navigation-compose:2.10.1")
    implementation("androidx.concurrent:concurrent-futures:1.2.0")
    implementation("com.caoccao.javet:javet-v8-android:6.0.3")
    implementation("org.bouncycastle:bcpkix-jdk18on:1.87")
    debugImplementation("androidx.compose.ui:ui-tooling")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
    androidTestImplementation(platform("androidx.compose:compose-bom:2026.05.01"))
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    androidTestImplementation("androidx.test:runner:1.7.0")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("com.squareup.okhttp3:mockwebserver3:5.3.2")
}
