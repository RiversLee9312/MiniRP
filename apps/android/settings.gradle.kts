pluginManagement {
    repositories {
        if ((System.getenv("MINIRP_MAVEN_MIRROR") ?: System.getenv("MINICHAT_MAVEN_MIRROR")) == "1")
            maven("https://maven.aliyun.com/repository/google")
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        if ((System.getenv("MINIRP_MAVEN_MIRROR") ?: System.getenv("MINICHAT_MAVEN_MIRROR")) == "1")
            maven("https://maven.aliyun.com/repository/google")
        google()
        mavenCentral()
    }
}

rootProject.name = "MiniRP"

include(":app")
