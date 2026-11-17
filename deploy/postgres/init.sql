-- One Postgres server, but a separate database per service.
-- Each service only has the connection string for its own database,
-- so the only way to get another service's data is through its API.
CREATE DATABASE classes_db;
CREATE DATABASE bookings_db;
