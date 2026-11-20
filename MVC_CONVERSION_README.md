# Medicount Chatbot

This package uses ASP.NET Core API controllers and static HTML/JavaScript, not Razor views. See DEPLOYMENT.txt for the current IIS configuration, database prerequisites and test instructions. The previous conversion notes were obsolete and have been replaced.

Entry routes: / and /preview(.html) for the preview; /widget(.html) for the chatbot. API routes: /api/session and /api/{choose,message,form,prechat,keepalive,event}. /healthz is a liveness check; /api/status reports configured billing/email modes.

The widget uses bearer-token sessions. Identity verification calls the SQL repository directly. All SQL consumers resolve the same ChatDb connection configuration. No real credentials are included.

The existing production-release gate remains in place. Use the documented staging configuration for sandbox.medicount.com. See TEST-RESULTS.txt for executed checks and remaining server acceptance tests.
