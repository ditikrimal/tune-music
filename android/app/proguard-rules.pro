# NewPipeExtractor uses Rhino (JavaScript) and reflection-based parsing
-keep class org.schabi.newpipe.extractor.** { *; }
-keep class org.mozilla.javascript.** { *; }
-dontwarn org.mozilla.javascript.**
-dontwarn javax.script.**
-dontwarn java.beans.**
-dontwarn jdk.dynalink.**

# Credential Manager (Continue with Google) loads its Play Services provider by reflection
-if class androidx.credentials.CredentialManager
-keep class androidx.credentials.playservices.** { *; }
