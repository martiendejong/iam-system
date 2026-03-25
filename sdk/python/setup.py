from setuptools import setup, find_packages

setup(
    name="iam-sdk",
    version="1.0.0",
    packages=find_packages(),
    install_requires=[
        "httpx>=0.25.0",
        "pydantic>=2.0.0",
    ],
    python_requires=">=3.9",
    description="Python SDK for IAM System - Identity and Access Management",
    author="IAM System",
    classifiers=[
        "Programming Language :: Python :: 3",
        "License :: OSI Approved :: MIT License",
    ],
)
