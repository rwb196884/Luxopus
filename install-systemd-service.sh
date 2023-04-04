#!/bin/sh

echo "Checking..."
ok=1

if [ ! -d "/etc/systemd/system" ]; then
	echo "Could not find /etc/systemd/system"
	ok=0
fi

if [ ! -e "Rwb.Luxopus.Systemc/unit" ]; then
	echo "Could not find Rwb.Luxopus.Systemc/unit"
	ok=0
fi

if [ ! -d "/etc" ]; then
	echo "Could not find /etc"
	ok=0
fi

if [ ! -d "/var" ]; then
	echo "Could not find /var"
	ok=0
fi

if [ $ok -eq 0 ]; then
	echo "Checks failed. Quitting."
	exit 1
fi

# Proceed to install.

# Get the install location.
luxopus_dir=$(dirname "$0")
if [ "luxopus_dir" != "/opt/luxopus"]; then
	echo "WARING: install directory is $pwd but the recommended location is /opt/luxopus."
	# update the unit file
	sed -i -e "s!ExecStart=/opt/luxopus!ExecStart=$luxopus_dir" "$luxopus_dir/Rwb.Luxopus.Systemd/unit" > "$luxopus_dir/Rwb.Luxopus.Systemd/unit"
fi

echo "Copying unit file to /etc/systemd/system/luxopus and systemctl reload-daemon."
cp "$luxopus_dir/Rwb.Luxopus.Systemd/unit" /etc/systemd/system/luxopus
systemctl reload-daemon

if [ ! -f /etc/lusopus ]; then
	echo "/etc/luxopus does not exists; copying from defualt configuration."
	echo "  WARNING! You need to edit /etc/luxopus!"
	cp "$luxopus_dir/Rwb.Luxopus.Console/appsettings.json" /etc/luxopus
fi


if [ ! -d "/var/opt/luxopus" ]; then
	if [ ! -d "/var/opt" ]; then
		echo "Making runtime storage directory at /var/opt"
		mkdir /var/opt
	fi
	echo "Making runtime storage directory at /var/opt/luxopus"
	mkdir /var/opt/luxopus
fi
# idea: run.sh 2>&1 | logger