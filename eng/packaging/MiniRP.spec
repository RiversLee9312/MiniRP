Name: minirp
Version: %{minirp_version}
Release: 1
Summary: MiniRP chat workspace
License: MIT
Requires: glibc >= 2.34, libgcc, libstdc++, libicu, openssl-libs, zlib
Recommends: xdg-utils, zenity, gtk3, nss, nspr, cups-libs, mesa-libgbm, alsa-lib, ffmpeg, poppler-utils
AutoReqProv: no
%global __os_install_post %{nil}
%global _build_id_links none
%global _buildhost minirp-build

%description
Local terminal and browser interfaces for MiniRP.

%install
mkdir -p "%{buildroot}"
cp -a "%{minirp_payload}/." "%{buildroot}/"

%files
%license /usr/share/licenses/minirp/LICENSE
/usr/lib/minirp
/usr/bin/minirp
/usr/share/applications/minirp.desktop
/usr/share/icons/hicolor/256x256/apps/minirp.png
