# The core's JNI exports encode the Kotlin class and method names.
-keepclasseswithmembers,allowoptimization class com.minirp.app.core.NativeBridge {
    native <methods>;
}

# Javet 6.0.3 has no consumer rules. V8 looks up Java callbacks, value classes
# and constructors by name, including types that are never called from Kotlin.
-keep class com.caoccao.javet.** { *; }
