# Luxopus

https://github.com/rwb196884/Luxopus

A more obvious name might have been `Octolux`; therefore I didn't choose it.

LUXpower / Octopus control

You require:
* a LUX Power inverter,
* an Octopus Energy account (and API key), and
* an installation of InfluxDB and an API key.

Time is always UTC, but the LUX inverter uses local time; use the setting `Lux--TimeZone`.

Basic idea:
* get data,
* use the data -- and your experience -- to make a plan for what to do in every half hour (or whatever) period,
* check that the interter's settings implement the plan.

A _service_ is a class that has an associated configuration section, it typically gets data and writes it to InfluxDB;  and

A a _job_ is a class with a method called `RunAsync` that is run on a `cron` schedule and uses some _services_.

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
# Paste it at https://github.com/settings/keys
git clone https://github.com/rwb196884/Luxopus.git
```

## Configuration

If `/etc/luxopus.config` exists then it will be used
however `appsettings.json` is searched for in the working directory, strangely.

To run the command line version in linux:
```
$ runConsole.sh
```

