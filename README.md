# GooglePhotoToPostcards
Sends daily a postcard from synced google photo albums.

# Manual
Please set all variables defined in .env file before running the application.
This file is only used for docker compose, locally running you must set it manually.

Follow https://github.com/f2calv/CasCap.Apis.GooglePhotos to setup google photo api.

We have some limitations with a container runtime, so first the application must be started locally.
After a first successfully login, you must map your Google.Apis.Auth.OAuth2 token into the container.
You will find the file in the folder specified in GPSC_CONFIGPATH.

Configure the .env file and the postcardconfig.json (see https://github.com/abertschi/postcards) file.
Adjusted the docker-compose.yaml to match your google account oauth2 token.

Run docker-compose, docker-compose up -d.

# Special thanks
C# googel photo API - https://github.com/f2calv/CasCap.Apis.GooglePhotos
Postcards python application - https://github.com/abertschi/postcards
