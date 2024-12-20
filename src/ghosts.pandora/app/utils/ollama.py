import requests
import app_logging
import time
from config.config import OLLAMA_API_URL, OLLAMA_TIMEOUT

logger = app_logging.setup_logger("app_logger")


def generate_document_with_ollama(prompt: str, model: str):
    """Generate a document using the Ollama API with a specified prompt and model."""

    logger.info("Sending request to Ollama model")

    payload = {"model": model, "prompt": prompt, "stream": False}
    logger.info("Payload sent to Ollama API: %s", payload)

    try:
        # Record the start time before making the request
        start_time = time.time()

        # Make the request to Ollama with a timeout
        response = requests.post(OLLAMA_API_URL, json=payload, timeout=OLLAMA_TIMEOUT)

        # Calculate the elapsed time
        elapsed_time = time.time() - start_time

        # Log the response received
        if response.status_code == 200:
            response_data = response.json()
            logger.info(
                "Response received from Ollama in %.2f seconds: %s",
                elapsed_time,
                response_data,
            )
            return response_data.get(
                "response", ""
            )
        else:
            logger.error(
                "Invalid response from Ollama: %s - %s",
                response.status_code,
                response.text,
            )
            return None
    except requests.Timeout:
        logger.warning("Ollama request timed out.")
    except Exception as ollama_err:
        logger.error("Error while using Ollama: %s", ollama_err)

    return None
