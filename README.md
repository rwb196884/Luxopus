# Luxopus

https://github.com/rwb196884/Luxopus

A more obvious name might have been `Octolux`; therefore I didn't choose it.

LUXpower / Octopus control

You require:
* a LUX Power inverter,
* an Octopus Energy account, and
* an installation of InfluxDB and an API key.

Time is always UTC, but the LUX inverter uses local time; use the setting `Lux--TimeZone`.

## Installation

InfluxDB and dotnet.

### Checkout from git

```
ssh-keygen -t ed25519 -C "github.email@address"
# save as /home/rwb/.ssh/id_github
# Fuck knows what this bloody shit is
eval "$(ssh-agent -s)"
ssh-add ~/.ssh/id_github
cat ~/.ssh/id_github.pub
# Paste it at 
git clone https://github.com/rwb196884/Luxopus.git
```

## Configuration

If `/etc/luxopus.config` exists then it will be used.

To run the command line version in linux:
```
$ runConsole.sh
```

appsettings.json is searched for in the working directory, strangely.
